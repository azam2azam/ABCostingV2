using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Data;

/// <summary>A hospital's accounting months.</summary>
public static class OpenPeriods
{
    public static IEnumerable<DateOnly> Months(DateOnly from, DateOnly toInclusive)
    {
        if (toInclusive < from) (from, toInclusive) = (toInclusive, from);
        for (var d = new DateOnly(from.Year, from.Month, 1);
             d <= new DateOnly(toInclusive.Year, toInclusive.Month, 1);
             d = d.AddMonths(1))
            yield return d;
    }

    /// <summary>
    /// Gives each hospital its own copy of the shared periods it uses, moves its runs, ledger and
    /// driver volumes onto the copy, then removes the shared ones. Encounters only count for a
    /// hospital with no periods of its own, which would otherwise lose those months.
    /// </summary>
    public const string MarkChildInvoiceLinesSql = """
        UPDATE t SET IsChildInvoice = 1 FROM [EncounterService] t
        JOIN [Encounter] e ON e.Id = t.EncounterId
        JOIN [InvoicePackageItem] i ON i.HospitalId = e.HospitalId
         AND t.SourceRef = CONCAT('INV-', i.TransactionType, '-', i.ChildInvoiceNo, '-', i.ChildInvoiceLineItemNo)
        WHERE t.IsChildInvoice = 0 AND i.ChildInvoiceNo IS NOT NULL AND i.ChildInvoiceNo NOT IN ('', '0');
        UPDATE t SET IsChildInvoice = 1 FROM [RevenueTransaction] t
        JOIN [Encounter] e ON e.Id = t.EncounterId
        JOIN [InvoicePackageItem] i ON i.HospitalId = e.HospitalId
         AND t.SourceRef = CONCAT('REV-', i.TransactionType, '-', i.ChildInvoiceNo, '-', i.ChildInvoiceLineItemNo)
        WHERE t.IsChildInvoice = 0 AND i.ChildInvoiceNo IS NOT NULL AND i.ChildInvoiceNo NOT IN ('', '0');
        """;

    public const string AssignSharedSql = """
        SELECT p.Id AS SharedId, h.Id AS HospitalId, NEWID() AS OwnId INTO #Owned
        FROM [Period] p CROSS JOIN [Hospital] h
        WHERE p.HospitalId IS NULL AND (
              EXISTS (SELECT 1 FROM [CalculationRun] r WHERE r.PeriodId = p.Id AND r.HospitalId = h.Id)
           OR EXISTS (SELECT 1 FROM [GLPosting] g WHERE g.PeriodId = p.Id AND g.HospitalId = h.Id)
           OR EXISTS (SELECT 1 FROM [DriverVolume] d JOIN [CostCenter] c ON c.Id = d.CostCenterId WHERE d.PeriodId = p.Id AND c.HospitalId = h.Id)
           OR (NOT EXISTS (SELECT 1 FROM [Period] own WHERE own.HospitalId = h.Id)
               AND EXISTS (SELECT 1 FROM [Encounter] e WHERE e.HospitalId = h.Id AND e.AdmitAt >= p.StartDate AND e.AdmitAt < DATEADD(day, 1, CAST(p.EndDate AS datetime2)))));
        INSERT [Period] (Id, Code, StartDate, EndDate, Days, Status, HospitalId)
        SELECT o.OwnId, p.Code, p.StartDate, p.EndDate, p.Days, p.Status, o.HospitalId FROM #Owned o JOIN [Period] p ON p.Id = o.SharedId;
        UPDATE r SET PeriodId = o.OwnId FROM [CalculationRun] r JOIN #Owned o ON o.SharedId = r.PeriodId AND o.HospitalId = r.HospitalId;
        UPDATE g SET PeriodId = o.OwnId FROM [GLPosting] g JOIN #Owned o ON o.SharedId = g.PeriodId AND o.HospitalId = g.HospitalId;
        UPDATE d SET PeriodId = o.OwnId FROM [DriverVolume] d JOIN [CostCenter] c ON c.Id = d.CostCenterId JOIN #Owned o ON o.SharedId = d.PeriodId AND o.HospitalId = c.HospitalId;
        DELETE p FROM [Period] p WHERE p.HospitalId IS NULL
          AND NOT EXISTS (SELECT 1 FROM [CalculationRun] r WHERE r.PeriodId = p.Id)
          AND NOT EXISTS (SELECT 1 FROM [GLPosting] g WHERE g.PeriodId = p.Id)
          AND NOT EXISTS (SELECT 1 FROM [DriverVolume] d WHERE d.PeriodId = p.Id);
        DROP TABLE #Owned;
        """;

    /// <summary>
    /// Facts for a month the hospital does not have yet add that month, named like "2026 Sep";
    /// existing periods are never stretched.
    /// </summary>
    public static async Task<string[]> EnsureHospitalMonthsAsync(VeritaDbContext db, Guid hospitalId, DateOnly from, DateOnly toInclusive, CancellationToken ct)
    {
        var owned = await db.Periods.Where(p => p.HospitalId == hospitalId).ToListAsync(ct);
        var codes = owned.Select(p => p.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        foreach (var month in Months(from, toInclusive))
        {
            var end = month.AddMonths(1).AddDays(-1);
            if (owned.Any(p => p.StartDate <= end && p.EndDate >= month)) continue;
            var code = month.ToString("yyyy MMM", CultureInfo.InvariantCulture);
            if (!codes.Add(code)) continue;
            db.Periods.Add(new Period
            {
                Id = IdGen.Sequential(), Code = code, StartDate = month, EndDate = end,
                Days = end.DayNumber - month.DayNumber + 1, Status = "OPEN", HospitalId = hospitalId,
            });
            created.Add(code);
        }
        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created.ToArray();
    }
}
