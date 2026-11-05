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

        var encounters = await (from e in db.Encounters.AsNoTracking()
                                join p in db.Patients on e.PatientId equals p.Id
                                where e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < end
                                orderby e.EncounterNo
                                select new ModelEncounter(e.Id, e.DepartmentId, e.ServiceLineId, e.ProviderId, e.PayerId, e.DrgId, e.PackageId, e.EncType, p.Segment, e.LengthOfStay))
            .ToListAsync(ct);
        var lines = await (from s in db.EncounterServices.AsNoTracking()
                           join e in db.Encounters on s.EncounterId equals e.Id
                           where e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < end
                           select new ModelLine(s.EncounterId, s.ProcedureId, s.CostCenterId, s.ProviderId, s.Quantity, s.SourceRef)).ToListAsync(ct);
        var revenue = await (from r in db.RevenueTransactions.AsNoTracking()
                             join e in db.Encounters on r.EncounterId equals e.Id
                             where e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < end
                             select new ModelRevenue(r.EncounterId, r.SourceRef, r.NetAmount)).ToListAsync(ct);

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
