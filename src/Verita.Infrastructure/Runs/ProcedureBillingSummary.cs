using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// Per procedure, encounter and doctor, what the doctor's billed procedure lines in a run billed, claimed and consumed, for
/// procedures not billed as or under a package. Stored so the doctor page reads it instead of a month of lines.
/// </summary>
/// <remarks>
/// A line counts as the package lines do: for a HIS hospital a counted invoice line that was not refunded in full. A line
/// naming a package in PackageProcedureId ("0" is none), and a package procedure's own line, belong to the package. The
/// doctor is the service line's (HIS PrimaryDoctorID). Consumables and medicine are the HIS consumption records tied to the
/// line: consumables at HIS floor-consumption UnitPrice (quantity × selling price), medicine at MedicinePrice × quantity.
/// </remarks>
public static class ProcedureBillingSummary
{
    private static string Tail(string sourceRef) => PackageBillingSummary.Tail(sourceRef);

    public static async Task<int> StoreAsync(VeritaDbContext db, CalculationRun run, CancellationToken ct)
    {
        var rows = await CountAsync(db, run, ct);
        await db.RunProcedureDoctors.Where(x => x.RunId == run.Id).ExecuteDeleteAsync(ct);
        db.RunProcedureDoctors.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public static async Task<List<RunProcedureDoctor>> CountAsync(VeritaDbContext db, CalculationRun run, CancellationToken ct)
    {
        var hid = run.HospitalId;
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == hid, ct);
        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, ct);
        var (start, end) = (period.StartDate.ToDateTime(TimeOnly.MinValue), period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var fromHis = !string.IsNullOrWhiteSpace(hospital.HisSetupId);

        var packageProcedures = (await (from pr in db.Procedures.AsNoTracking()
                                        join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                                        where pr.HospitalId == hid
                                        select pr.Id).ToListAsync(ct)).ToHashSet();
        var packageCodes = (await db.Packages.AsNoTracking().Where(k => k.HospitalId == hid).Select(k => k.Code).ToListAsync(ct)).ToHashSet();

        var counted = db.RevenueTransactions.AsNoTracking().Where(HisInvoiceRules.Counts).Where(r => r.PostedAt >= start && r.PostedAt < end);
        var revenue = (await (fromHis ? counted : db.RevenueTransactions.AsNoTracking())
                .Where(r => r.ProcedureId != null && db.EncounterCosts.Any(ec => ec.RunId == run.Id && ec.EncounterId == r.EncounterId))
                .Select(r => new { r.EncounterId, r.SourceRef, ProcedureId = r.ProcedureId!.Value, r.PackageProcedureId, r.NetAmount, r.RefundedAmount, r.TaxAmount,
                    r.ClaimedAmount, r.SettledAmount, r.RejectedAmount, r.HisIsClaimed })
                .ToListAsync(ct))
            .Where(r => !fromHis || r.RefundedAmount == null || r.NetAmount <= 0m || r.RefundedAmount < r.NetAmount)
            .Where(r => !packageProcedures.Contains(r.ProcedureId) && !(r.PackageProcedureId is { Length: > 0 } c && c != "0" && packageCodes.Contains(c)))
            .ToList();
        if (revenue.Count == 0) return [];
        var claimFlagKnown = revenue.Any(r => r.HisIsClaimed != null);

        var services = (await (from es in db.EncounterServices.AsNoTracking()
                               join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                               where ec.RunId == run.Id
                               select new { es.EncounterId, es.SourceRef, es.ProviderId, es.Quantity }).ToListAsync(ct))
            .GroupBy(x => (x.EncounterId, Tail(x.SourceRef)))
            .ToDictionary(g => g.Key, g => (Provider: g.Select(x => x.ProviderId).FirstOrDefault(x => x != null), Quantity: g.Sum(x => x.Quantity)));

        var lineKeys = revenue.Select(r => (r.EncounterId, Tail(r.SourceRef))).ToHashSet();
        var encIds = revenue.Select(r => r.EncounterId).Distinct().ToList();
        var prices = await db.MedicinePrices.AsNoTracking().Where(m => m.HospitalId == hid && m.StdSellingPrice != null)
            .ToDictionaryAsync(m => m.MedicineCode, m => m.StdSellingPrice!.Value, ct);
        var consumed = new Dictionary<(Guid, string), (decimal Consumables, decimal Medicine)>();
        foreach (var c in await db.ConsumptionItems.AsNoTracking().Where(c => c.EncounterId != null && encIds.Contains(c.EncounterId.Value) && c.ServiceRef != null)
                     .Select(c => new { Encounter = c.EncounterId!.Value, c.ServiceRef, c.Category, c.ItemKey, c.Quantity, c.HisUnitPrice }).ToListAsync(ct))
        {
            var key = (c.Encounter, Tail(c.ServiceRef!));
            if (!lineKeys.Contains(key)) continue;
            var v = consumed.GetValueOrDefault(key);
            consumed[key] = c.Category == "MEDICINE"
                ? (v.Consumables, v.Medicine + c.Quantity * prices.GetValueOrDefault(c.ItemKey))
                : (v.Consumables + (c.HisUnitPrice ?? 0m), v.Medicine);
        }

        return revenue
            .Select(r => (Line: r, Key: (r.EncounterId, Tail(r.SourceRef))))
            .GroupBy(x => (x.Line.ProcedureId, x.Line.EncounterId, Provider: services.GetValueOrDefault(x.Key).Provider))
            .Select(g =>
            {
                var keys = g.Select(x => x.Key).Distinct().ToList();
                return new RunProcedureDoctor
                {
                    Id = IdGen.Sequential(), RunId = run.Id, ProcedureId = g.Key.ProcedureId, EncounterId = g.Key.EncounterId, ProviderId = g.Key.Provider,
                    Performed = Money.R6(keys.Sum(k => services.TryGetValue(k, out var sv) ? sv.Quantity : 1m)),
                    FeeBilled = Money.R4(g.Sum(x => x.Line.NetAmount - (x.Line.RefundedAmount ?? 0m) + (x.Line.TaxAmount ?? 0m))),
                    Claimed = Money.R4(g.Sum(x => x.Line.ClaimedAmount ?? 0m)),
                    ClaimsReceived = claimFlagKnown ? Money.R4(g.Sum(x => x.Line.HisIsClaimed == true ? x.Line.ClaimedAmount ?? 0m : 0m)) : null,
                    Settled = Money.R4(g.Sum(x => x.Line.SettledAmount ?? 0m)), Rejected = Money.R4(g.Sum(x => x.Line.RejectedAmount ?? 0m)),
                    Consumables = Money.R4(keys.Sum(k => consumed.GetValueOrDefault(k).Consumables)),
                    Medicine = Money.R4(keys.Sum(k => consumed.GetValueOrDefault(k).Medicine)),
                };
            }).ToList();
    }
}
