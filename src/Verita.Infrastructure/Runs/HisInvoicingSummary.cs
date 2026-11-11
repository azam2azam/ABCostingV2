using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// The period's HIS invoice counts by invoice type. Counting distinct invoices over a month of lines is slow,
/// so a run stores the result and HIS Invoicing reads it. A period with no run still counts live.
/// </summary>
public static class HisInvoicingSummary
{
    public const int TotalType = 0;

    private sealed record Raw(int? InvoiceType, int Invoices, int Encounters, int Patients, decimal? Refunded, decimal? Revenue, long? MinInvoiceNo, long? MaxInvoiceNo);

    /// <summary>Counts the period live. The total row has InvoiceType 0.</summary>
    public static async Task<List<RunHisInvoicing>> CountAsync(VeritaDbContext db, Guid hospitalId, DateOnly startDate, DateOnly endDate, Guid runId, CancellationToken ct)
    {
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var end = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // SourceRef is REV-{transactionType}-{invoiceNo}-{line}; dropping the line leaves the invoice.
        // The same lines a run counts: see HisInvoiceRules.
        // ROLLUP adds the total as a row with no type. Its counts are distinct, not the sum of the
        // rows, because a patient can have invoices of both types.
        var rows = await db.Database.SqlQuery<Raw>($"""
            SELECT t.HisInvoiceType AS InvoiceType,
                   COUNT(DISTINCT LEFT(t.SourceRef, LEN(t.SourceRef) - CHARINDEX('-', REVERSE(t.SourceRef)))) AS Invoices,
                   COUNT(DISTINCT t.EncounterId) AS Encounters,
                   COUNT(DISTINCT e.PatientId) AS Patients,
                   SUM(ISNULL(t.RefundedAmount, 0)) AS Refunded,
                   SUM(t.NetAmount - ISNULL(t.RefundedAmount, 0)) AS Revenue,
                   MIN(TRY_CAST(PARSENAME(REPLACE(t.SourceRef, '-', '.'), 2) AS bigint)) AS MinInvoiceNo,
                   MAX(TRY_CAST(PARSENAME(REPLACE(t.SourceRef, '-', '.'), 2) AS bigint)) AS MaxInvoiceNo
            FROM RevenueTransaction t
            INNER JOIN Encounter e ON e.Id = t.EncounterId
            WHERE e.HospitalId = {hospitalId} AND t.PostedAt >= {start} AND t.PostedAt < {end}
              AND t.HisLineStatus = 2 AND t.HisInvoiceType IN (1, 75)
            GROUP BY ROLLUP(t.HisInvoiceType)
            """).ToListAsync(ct);
        return rows.Select(x => new RunHisInvoicing
        {
            Id = IdGen.Sequential(), RunId = runId, InvoiceType = x.InvoiceType ?? TotalType, Invoices = x.Invoices, Encounters = x.Encounters,
            Patients = x.Patients, Refunded = x.Refunded ?? 0m, Revenue = x.Revenue ?? 0m, MinInvoiceNo = x.MinInvoiceNo, MaxInvoiceNo = x.MaxInvoiceNo,
        }).ToList();
    }

    /// <summary>Counts the run's period and stores it with the run, replacing what it held.</summary>
    public static async Task<List<RunHisInvoicing>> StoreAsync(VeritaDbContext db, CalculationRun run, CancellationToken ct)
    {
        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, ct);
        var rows = await CountAsync(db, run.HospitalId, period.StartDate, period.EndDate, run.Id, ct);
        await db.RunHisInvoicings.Where(x => x.RunId == run.Id).ExecuteDeleteAsync(ct);
        db.RunHisInvoicings.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows;
    }
}
