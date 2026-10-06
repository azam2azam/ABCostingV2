using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// Loads everything a run needs, once (ARCH-5, FR-2.9). Every effective-dated lookup — rates,
/// capacities, pools, drivers, rules, activities, definitions, mappings — is resolved here at the
/// period end, so the whole run sees one consistent world (FR-1.23). Master data comes through EF
/// Core without tracking; transactions are streamed separately with Dapper.
/// </summary>
public sealed class RunContextLoader(VeritaDbContext db)
{
    public async Task<RunContext> LoadAsync(Guid runId, ScenarioOverrides? overrides = null, CancellationToken ct = default, int distributionScale = 4)
    {
        overrides ??= new ScenarioOverrides();
        var run = await db.CalculationRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == run.HospitalId, ct);
        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, ct);
        var version = await db.CostingVersions.AsNoTracking().FirstAsync(v => v.Id == run.CostingVersionId, ct);
        var asOf = period.EndDate;
        var hid = run.HospitalId;

        var ccs = (await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == hid).ToListAsync(ct))
            .OrderBy(c => c.StepDownSeq).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new CcInfo(c.Id, c.Code, c.Name, c.CcType, c.StepDownSeq, c.DepartmentId)).ToList();

        // ── Resources with exactly the rate and capacity effective at the as-of date ──
        var resources = await db.Resources.AsNoTracking().Where(r => r.HospitalId == hid && r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
        var resIds = resources.Select(r => r.Id).ToList();
        var rates = await db.ResourceRates.AsNoTracking().Where(r => resIds.Contains(r.ResourceId)).ToListAsync(ct);
        var caps = await db.ResourceCapacities.AsNoTracking().Where(c => resIds.Contains(c.ResourceId)).ToListAsync(ct);
        var effRates = rates.Where(r => r.IsEffective(asOf)).GroupBy(r => r.ResourceId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First());
        var effCaps = caps.Where(c => c.IsEffective(asOf)).GroupBy(c => c.ResourceId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.EffectiveFrom).First());

        var resInfo = new Dictionary<Guid, ResInfo>();
        foreach (var r in resources)
        {
            effRates.TryGetValue(r.Id, out var rate);
            effCaps.TryGetValue(r.Id, out var cap);
            var rm = overrides.Get(overrides.ResourceRate, r.Id);
            var cm = overrides.Get(overrides.ResourceCapacity, r.Id);
            resInfo[r.Id] = new ResInfo(r.Id, r.Code, r.Name, r.ResourceType, r.Uom, r.IsCapacityDriven, r.CostCenterId, r.CostPoolId,
                (rate?.UnitCost ?? 0m) * rm, rate?.Id,
                cap is null ? null : new CapacityInfo(cap.TheoreticalMinutes * cm, cap.PracticalMinutes * cm, cap.PracticalRatio, cap.AnnualCost));
        }

        // ── Pools; CR-4: a pool is consumption-stream when any resource draws from it ──
        var funded = resources.Where(r => r.CostPoolId is not null).Select(r => r.CostPoolId!.Value).ToHashSet();
        var pools = (await db.CostPools.AsNoTracking().Where(p => p.HospitalId == hid && p.Status == "PUBLISHED").ToListAsync(ct))
            .Where(p => p.IsEffective(asOf)).OrderBy(p => p.Code, StringComparer.Ordinal)
            .Select(p => new PoolInfo(p.Id, p.Code, p.Name, p.CostCenterId, p.Classification, p.AllocMethod, p.PrimaryDriverId, !funded.Contains(p.Id)))
            .ToList();

        var drivers = (await db.CostDrivers.AsNoTracking().Where(d => d.HospitalId == hid && d.Status == "PUBLISHED").ToListAsync(ct))
            .Where(d => d.IsEffective(asOf))
            .ToDictionary(d => d.Id, d => new DriverInfo(d.Id, d.Code, d.CatalogCode, d.Name, d.Unit, d.DriverType, d.Formula));

        var ccIds = ccs.Select(c => c.Id).ToList();
        var volumes = (await db.DriverVolumes.AsNoTracking().Where(v => v.PeriodId == run.PeriodId && ccIds.Contains(v.CostCenterId)).ToListAsync(ct))
            .GroupBy(v => (v.DriverId, v.CostCenterId)).ToDictionary(g => g.Key, g => g.Sum(v => v.Quantity));

        var poolCc = await db.CostPools.AsNoTracking().Where(p => p.HospitalId == hid).ToDictionaryAsync(p => p.Id, p => p.CostCenterId, ct);
        var ruleRows = (await db.AllocationRules.AsNoTracking().Where(r => r.Status == "PUBLISHED").ToListAsync(ct))
            .Where(r => poolCc.ContainsKey(r.SourcePoolId) && r.IsEffective(asOf)).OrderBy(r => r.Stage).ThenBy(r => r.Sequence).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        var ruleIds = ruleRows.Select(r => r.Id).ToList();
        var targets = (await db.AllocationRuleTargets.AsNoTracking().Where(t => ruleIds.Contains(t.RuleId)).ToListAsync(ct)).ToLookup(t => t.RuleId);
        var rules = ruleRows.Select(r => new RuleInfo(r.Id, r.Code, r.Name, r.SourcePoolId, poolCc[r.SourcePoolId], r.DriverId, r.Method, r.Methodology,
            r.Stage, r.Sequence, r.PoolShare, r.Formula,
            targets[r.Id].Select(t => new RuleTargetInfo(t.CostCenterId, t.Percentage, t.Weight)).ToList())).ToList();

        // ── Activities and their standard recipes ──
        var acts = (await db.Activities.AsNoTracking().Where(a => a.HospitalId == hid && a.Status == "PUBLISHED").ToListAsync(ct)).Where(a => a.IsEffective(asOf)).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
        var actIds = acts.Select(a => a.Id).ToList();
        var recipes = (await db.ActivityResources.AsNoTracking().Where(x => actIds.Contains(x.ActivityId)).ToListAsync(ct)).ToLookup(x => x.ActivityId);
        var actDrivers = (await db.ActivityDrivers.AsNoTracking().Where(x => actIds.Contains(x.ActivityId)).ToListAsync(ct)).ToLookup(x => x.ActivityId);
        var activities = acts.ToDictionary(a => a.Id, a => new ActInfo(a.Id, a.Code, a.CatalogCode, a.Name, a.Category, a.CostCenterId, a.DepartmentId,
            a.Directness, a.StdDuration, a.MinDuration, a.MaxDuration,
            recipes[a.Id].Select(r => new RecipeLine(r.ResourceId, r.StdQuantity, r.Uom)).ToList(), actDrivers[a.Id].FirstOrDefault()?.DriverId));

        // ── Procedures under the run's costing version only (FR-5.3) ──
        var procs = await db.Procedures.AsNoTracking().Where(p => p.HospitalId == hid && p.IsActive).OrderBy(p => p.Code).ToListAsync(ct);
        var procIds = procs.Select(p => p.Id).ToList();
        var defs = await db.CostingDefinitions.AsNoTracking().Where(d => d.CostingVersionId == run.CostingVersionId && procIds.Contains(d.ProcedureId)).ToListAsync(ct);
        var defIds = defs.Select(d => d.Id).ToList();
        var actLines = (await db.ProcedureActivityLines.AsNoTracking().Where(l => defIds.Contains(l.DefinitionId)).ToListAsync(ct)).ToLookup(l => l.DefinitionId);
        var resLines = (await db.ProcedureResourceLines.AsNoTracking().Where(l => defIds.Contains(l.DefinitionId)).ToListAsync(ct)).ToLookup(l => l.DefinitionId);
        var defByProc = defs.ToDictionary(d => d.ProcedureId);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == hid).ToListAsync(ct)).Where(r => r.IsEffective(asOf))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        var procedures = procs.ToDictionary(p => p.Id, p =>
        {
            defByProc.TryGetValue(p.Id, out var def);
            return new ProcInfo(p.Id, p.Code, p.CatalogCode, p.Name, p.ProcType, p.Category, p.Complexity, p.DepartmentId, p.ServiceLineId,
                prices.GetValueOrDefault(p.Id),
                def is null ? [] : actLines[def.Id].Select(l => new ProcActivityLine(l.ActivityId, l.Quantity)).ToList(),
                def is null ? [] : resLines[def.Id].Select(l => new ProcResourceLine(l.ResourceId, l.Quantity, l.Uom)).ToList());
        });

        var pkgs = await db.Packages.AsNoTracking().Where(p => p.HospitalId == hid).ToListAsync(ct);
        var pkgIds = pkgs.Select(p => p.Id).ToList();
        var pkgComps = (await db.PackageComponents.AsNoTracking().Where(c => pkgIds.Contains(c.PackageId)).ToListAsync(ct)).ToLookup(c => c.PackageId);
        var packages = pkgs.ToDictionary(p => p.Id, p => new PackageInfo(p.Id, p.Code, p.Name, p.Price, pkgComps[p.Id].Select(c => (c.ProcedureId, c.Quantity)).ToList()));

        // ── Ledger ──
        var accounts = await db.GLAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var postings = (await db.GLPostings.AsNoTracking().Where(p => p.HospitalId == hid && p.PeriodId == run.PeriodId).ToListAsync(ct))
            .Select(p => new PostingRow(p.GLAccountId, accounts[p.GLAccountId].Code, accounts[p.GLAccountId].Name, p.CostCenterId, p.Amount)).ToList();
        var ccName = ccs.ToDictionary(c => c.Id, c => c.Name);
        var mappings = (await db.GLMappings.AsNoTracking().Where(m => ccIds.Contains(m.CostCenterId)).ToListAsync(ct)).Where(m => m.IsEffective(asOf))
            .Select(m => new MappingRow(m.GLAccountId, m.CostCenterId, m.CostPoolId, m.Weight, $"{accounts[m.GLAccountId].Code} {accounts[m.GLAccountId].Name}", ccName.GetValueOrDefault(m.CostCenterId, ""))).ToList();

        // ── Clinical scope: encounters admitted in the period (BRD A-2) ──
        var (start, endExclusive) = PeriodBounds(period);
        var encounters = await db.Encounters.AsNoTracking().Where(e => e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < endExclusive)
            .OrderBy(e => e.EncounterNo)
            .Select(e => new EncounterRow(e.Id, e.LengthOfStay, e.PayerId, e.DrgId, e.PackageId, e.IsSurgical)).ToListAsync(ct);
        var services = await (from s in db.EncounterServices.AsNoTracking()
                              join e in db.Encounters on s.EncounterId equals e.Id
                              where e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < endExclusive
                              orderby s.SourceRef
                              select new ServiceRow(s.Id, s.EncounterId, s.ProcedureId, s.CostCenterId, s.ProviderId, s.Quantity)).ToListAsync(ct);
        var revenue = await (from r in db.RevenueTransactions.AsNoTracking()
                             join e in db.Encounters on r.EncounterId equals e.Id
                             where e.HospitalId == hid && e.AdmitAt >= start && e.AdmitAt < endExclusive
                             select new RevenueRow(r.EncounterId, r.ProcedureId, r.PayerId, r.NetAmount)).ToListAsync(ct);
        var spans = rates.Select(r => new RateSpan(r.ResourceId, r.EffectiveFrom, r.EffectiveTo)).ToList();

        return new RunContext
        {
            RunId = run.Id, RunCode = run.Code, HospitalId = hid, HospitalCode = hospital.Code, HospitalName = hospital.Name,
            PeriodId = period.Id, PeriodCode = period.Code, PeriodDays = period.Days, PeriodStart = period.StartDate, PeriodEnd = period.EndDate,
            CostingVersionId = version.Id, CostingVersionCode = version.Code,
            CostCenters = ccs, Pools = pools, Resources = resInfo, Drivers = drivers, DriverVolumes = volumes, Rules = rules,
            Activities = activities, Procedures = procedures, Packages = packages, Postings = postings, Mappings = mappings,
            Encounters = encounters, Services = services, Revenue = revenue, RateSpans = spans, Overrides = overrides,
            DistributionScale = distributionScale,
        };
    }

    public static (DateTime Start, DateTime EndExclusive) PeriodBounds(Period p) =>
        (p.StartDate.ToDateTime(TimeOnly.MinValue), p.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));

    /// <summary>
    /// Streams the period's transactions with keyset pagination on the clustered key (ARCH-6):
    /// OFFSET would re-walk the rows it skips and turn a linear scan quadratic.
    /// </summary>
    public static IEnumerable<TxnRow> StreamTransactions(string connectionString, Guid hospitalId, DateTime start, DateTime endExclusive, int pageSize = 20000)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        Guid? lastEnc = null, lastId = null;
        const string sql = """
            SELECT TOP (@pageSize) t.EncounterId, t.EncounterServiceId, t.ActivityId, t.ResourceId, t.CostCenterId, t.Quantity, t.Uom, t.SourceRef, t.Id
            FROM ActivityTransaction t
            JOIN Encounter e ON e.Id = t.EncounterId
            WHERE e.HospitalId = @hospitalId AND e.AdmitAt >= @start AND e.AdmitAt < @endExclusive
              AND (@lastEnc IS NULL OR t.EncounterId > @lastEnc OR (t.EncounterId = @lastEnc AND t.Id > @lastId))
            ORDER BY t.EncounterId, t.Id
            """;
        while (true)
        {
            var page = conn.Query<(Guid EncounterId, Guid? EncounterServiceId, Guid ActivityId, Guid ResourceId, Guid CostCenterId, decimal Quantity, string Uom, string SourceRef, Guid Id)>(
                sql, new { pageSize, hospitalId, start, endExclusive, lastEnc, lastId }, commandTimeout: 300).AsList();
            foreach (var r in page)
                yield return new TxnRow(r.EncounterId, r.EncounterServiceId, r.ActivityId, r.ResourceId, r.CostCenterId, r.Quantity, r.Uom, r.SourceRef);
            if (page.Count < pageSize) yield break;
            lastEnc = page[^1].EncounterId;
            lastId = page[^1].Id;
        }
    }
}
