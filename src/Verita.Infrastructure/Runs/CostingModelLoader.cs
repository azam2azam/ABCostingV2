using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>Reads what a costing-model run needs: the period's encounters and the run's costing version.</summary>
public sealed class CostingModelLoader(VeritaDbContext db)
{
    public const string Direct = "DIRECT";
    public const string Indirect = "INDIRECT";
    public const string PackageDirect = "PACKAGE_DIRECT";
    public const string ZeroFixed = "ZERO_FIXED";

    /// <summary>A hospital is costed by its costing model once that version holds Direct or Indirect costing for it; package costing alone does not count.</summary>
    public static async Task<bool> HasSetupAsync(VeritaDbContext db, Guid hospitalId, Guid versionId, CancellationToken ct) =>
        await db.ProcedureTypeCostings.AnyAsync(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId && x.Stream != PackageDirect && x.Stream != ZeroFixed, ct)
        || await db.ProcedureMargins.AnyAsync(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId && x.Stream != PackageDirect && x.Stream != ZeroFixed, ct)
        || await db.EncounterOverheads.AnyAsync(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId, ct);

    public async Task<CostingModelContext> LoadAsync(CalculationRun run, CancellationToken ct)
    {
        var hid = run.HospitalId;
        var vid = run.CostingVersionId;
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == hid, ct);
        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, ct);
        var version = await db.CostingVersions.AsNoTracking().FirstAsync(v => v.Id == vid, ct);
        var start = period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // A HIS hospital's run takes the invoices dated in the period that count, whenever the patient was
        // admitted, and only their lines. Anything else is costed on the encounters admitted in the period.
        var fromHis = !string.IsNullOrWhiteSpace(hospital.HisSetupId);
        var counted = db.RevenueTransactions.AsNoTracking().Where(HisInvoiceRules.Counts).Where(r => r.PostedAt >= start && r.PostedAt < end);
        var inRun = fromHis
            ? db.Encounters.AsNoTracking().Where(e => e.HospitalId == hid && counted.Any(r => r.EncounterId == e.Id))
            : db.Encounters.AsNoTracking().Where(e => e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < end);

        var encounters = await (from e in inRun
                                join p in db.Patients on e.PatientId equals p.Id
                                orderby e.EncounterNo
                                select new ModelEncounter(e.Id, e.DepartmentId, e.ServiceLineId, e.ProviderId, e.PayerId, e.DrgId, e.PackageId, e.EncType, p.Segment, e.LengthOfStay))
            .ToListAsync(ct);
        var revenue = await (from r in fromHis ? counted : db.RevenueTransactions.AsNoTracking()
                             join e in inRun on r.EncounterId equals e.Id
                             select new ModelRevenue(r.EncounterId, r.SourceRef, r.NetAmount - (r.RefundedAmount ?? 0m), r.HisUnitPrice == 0m)).ToListAsync(ct);
        var lines = await (from s in db.EncounterServices.AsNoTracking()
                           join e in inRun on s.EncounterId equals e.Id
                           select new ModelLine(s.EncounterId, s.ProcedureId, s.CostCenterId, s.ProviderId, s.Quantity, s.SourceRef, false)).ToListAsync(ct);
        if (fromHis)
        {
            var countedLines = revenue.GroupBy(r => (r.EncounterId, CostingModelPipeline.LineRef(r.SourceRef))).ToDictionary(g => g.Key, g => g.Any(r => r.ZeroPrice));
            // A line refunded in full keeps its revenue row (now nil) but is not costed: the service was not given.
            var refunded = (await (from r in counted.Where(HisInvoiceRules.FullyRefunded) join e in inRun on r.EncounterId equals e.Id select new { r.EncounterId, r.SourceRef }).ToListAsync(ct))
                .Select(r => (r.EncounterId, CostingModelPipeline.LineRef(r.SourceRef))).ToHashSet();
            foreach (var key in refunded) countedLines.Remove(key);
            lines = lines.Where(l => countedLines.ContainsKey((l.EncounterId, CostingModelPipeline.LineRef(l.SourceRef))))
                .Select(l => l with { ZeroPrice = countedLines[(l.EncounterId, CostingModelPipeline.LineRef(l.SourceRef))] }).ToList();
        }

        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == hid).ToListAsync(ct))
            .Where(r => r.IsEffective(period.EndDate))
            .GroupBy(r => r.ProcedureId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        var procedures = (await db.Procedures.AsNoTracking().Where(p => p.HospitalId == hid).Select(p => new { p.Id, p.Name, p.Category, p.ProcType }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => new ModelProcedure(p.Id, p.Name, string.Equals(p.Category, "MEDICINE", StringComparison.OrdinalIgnoreCase), p.ProcType, prices.GetValueOrDefault(p.Id)));

        var types = await db.ProcedureTypeCostings.AsNoTracking().Where(x => x.HospitalId == hid && x.CostingVersionId == vid).ToListAsync(ct);
        var items = await db.ProcedureMargins.AsNoTracking().Where(x => x.HospitalId == hid && x.CostingVersionId == vid).ToListAsync(ct);
        Dictionary<string, decimal> TypesOf(string stream) => types.Where(x => x.Stream == stream).ToDictionary(x => x.ProcType, x => x.MarginPct, StringComparer.OrdinalIgnoreCase);
        Dictionary<Guid, decimal> ItemsOf(string stream) => items.Where(x => x.Stream == stream).ToDictionary(x => x.ProcedureId, x => x.MarginPct);

        var resources = db.Resources.AsNoTracking().Where(r => r.HospitalId == hid && r.IsActive);
        if (!string.IsNullOrWhiteSpace(hospital.HisSetupId)) resources = resources.Where(r => r.Code.Contains(".RES-MD-"));

        return new CostingModelContext
        {
            RunId = run.Id, HospitalId = hid, HospitalName = hospital.Name, PeriodCode = period.Code, CostingVersionCode = version.Code, CalculatedAt = DateTime.UtcNow,
            Encounters = encounters, Lines = lines, Revenue = revenue, Procedures = procedures,
            DirectTypes = TypesOf(Direct), DirectItems = ItemsOf(Direct),
            ZeroFixedTypes = types.Where(x => x.Stream == ZeroFixed).ToDictionary(x => x.ProcType, x => x.FixedAmount ?? 0m, StringComparer.OrdinalIgnoreCase),
            ZeroFixedItems = items.Where(x => x.Stream == ZeroFixed).ToDictionary(x => x.ProcedureId, x => x.FixedAmount ?? 0m),
            PackageProcedures = (await (from pr in db.Procedures.AsNoTracking()
                                        join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                                        where pr.HospitalId == hid
                                        select pr.Id).ToListAsync(ct)).ToHashSet(),
            PackageDirectTypes = TypesOf(PackageDirect), PackageDirectItems = ItemsOf(PackageDirect),
            IndirectTypes = TypesOf(Indirect), IndirectItems = ItemsOf(Indirect),
            EncounterOverheads = await db.EncounterOverheads.AsNoTracking().Where(x => x.HospitalId == hid && x.CostingVersionId == vid)
                .Select(x => new ModelEncounterOverhead(x.OverheadPct, x.OverheadAmount)).ToListAsync(ct),
            DoctorResource = await db.Providers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ResourceId, ct),
            ListedResources = await resources.ToDictionaryAsync(r => r.Id, r => r.Name, ct),
            Labels = new Dictionary<string, Dictionary<string, string>>
            {
                ["department"] = await db.Departments.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => x.Name, ct),
                ["serviceLine"] = await db.ServiceLines.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => x.Name, ct),
                ["physician"] = await db.Providers.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => $"{x.Name} ({x.Specialty})", ct),
                ["payer"] = await db.Payers.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => x.Name, ct),
                ["package"] = await db.Packages.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => x.Name, ct),
                ["drg"] = await db.Drgs.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => $"{x.Code} {x.Name}", ct),
                ["costCentre"] = await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == hid).ToDictionaryAsync(x => x.Id.ToString(), x => x.Name, ct),
            },
        };
    }
}
