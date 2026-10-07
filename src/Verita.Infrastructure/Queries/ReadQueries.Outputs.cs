using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    public sealed record ComponentDto(Guid Id, string ComponentType, string CostNature, string Label, decimal Quantity, string Uom, string? UnitCost,
        decimal Amount, decimal Share, string CalcExpression, string? SourceSystem, string? SourceRef, Guid? ResourceId, Guid? ActivityId);
    public sealed record Provenance(string Run, string RunStatus, string CostingVersion, string Hospital, string Period, string TriggeredBy,
        string? ApprovedBy, DateTime? PublishedAt, DateTime? CalculatedAt);

    private static Provenance Prov(ScopedRun s) => new(s.Run.Code, s.Run.Status, s.Version.Code, s.Hospital.Name, s.Period.Code, s.Run.TriggeredBy,
        s.Run.ApprovedBy, s.Run.PublishedAt, s.Run.FinishedAt);

    private ComponentDto[] Components(Principal p, IEnumerable<CostComponent> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(c => c.Amount);
        return list.OrderBy(c => Array.IndexOf(Catalogue.ComponentDisplayOrder, c.ComponentType)).ThenByDescending(c => c.Amount)
            .Select(c => new ComponentDto(c.Id, c.ComponentType, c.CostNature, c.Label, c.Quantity, c.Uom,
                c.ComponentType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : Money.R6(c.UnitCost).ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.Amount, Share(c.Amount, total), c.CalcExpression, c.SourceSystem, c.SourceRef, c.ResourceId, c.ActivityId)).ToArray();
    }

    // ═══ Procedures and the cost explainer (FR-11.7, AC primary test 23.1) ═══════════

    public sealed record ProcedureRow(Guid Id, string Code, string CatalogCode, string Name, string Category, string ProcType, string Department,
        string? ServiceLine, decimal Volume, decimal StandardUnit, decimal ActualUnit, decimal ActualTotal, decimal Revenue, decimal Variance,
        decimal VariancePct, decimal ListPrice, string State);
    public sealed record ChainActivity(Guid ActivityId, string Activity, decimal Quantity, string CostCentre, decimal OverheadPerOccurrence,
        AllocationDto[] OverheadSources, RecipeLineDto[] Resources);
    public sealed record SourceTxn(Guid Id, string EncounterNo, string Activity, string Resource, decimal Quantity, string Uom, DateTime OccurredAt,
        string SourceSystem, string SourceRef);
    public sealed record CrossHospital(string Hospital, string HospitalName, string Run, string RunStatus, Guid ProcedureId, decimal StandardUnit,
        decimal ActualUnit, decimal Volume);
    public sealed record ProcedureDetail(ProcedureRow Procedure, ComponentDto[] StandardComponents, decimal StandardTotal, ProcedureCost? Actual,
        VarianceDetail? Variance, ChainActivity[] Chain, SourceTxn[] SampleTransactions, CrossHospital[] AcrossHospitals, Provenance Provenance);

    public async Task<ProcedureRow[]> ProceduresAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var sls = await db.ServiceLines.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var costs = (await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id).ToListAsync(ct)).ToLookup(c => c.ProcedureId);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct)).Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.First().ListPrice);
        var showRevenue = p.Can("revenue.view");
        return (await db.Procedures.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct))
            .Where(x => DeptAllowed(p, deptCodes, x.DepartmentId))
            .Select(x =>
            {
                var std = costs[x.Id].FirstOrDefault(c => c.CostType == "STANDARD");
                var act = costs[x.Id].FirstOrDefault(c => c.CostType == "ACTUAL");
                var vol = act?.Volume ?? 0;
                var stdTotal = (std?.UnitCost ?? 0) * vol;
                var variance = act is null ? 0 : Money.R4(act.TotalCost - stdTotal);
                var state = std is null ? "NOT_COSTED" : act is null ? "NO_CASES" : "COSTED";
                return new ProcedureRow(x.Id, x.Code, x.CatalogCode, x.Name, x.Category, x.ProcType, depts[x.DepartmentId].Name,
                    x.ServiceLineId is { } sl ? sls.GetValueOrDefault(sl) : null, vol, std?.UnitCost ?? 0, act?.UnitCost ?? 0, act?.TotalCost ?? 0,
                    showRevenue ? act?.Revenue ?? 0 : 0, variance, stdTotal == 0 ? 0 : Money.R4(variance / stdTotal * 100m), prices.GetValueOrDefault(x.Id), state);
            }).ToArray();
    }

    public async Task<ProcedureDetail> ProcedureAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await ProceduresAsync(s, p, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Procedure");
        var comps = await db.CostComponents.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.Scope == "PROCEDURE" && c.ScopeId == id).ToListAsync(ct);
        var actual = await db.ProcedureCosts.AsNoTracking().FirstOrDefaultAsync(c => c.RunId == s.Run.Id && c.ProcedureId == id && c.CostType == "ACTUAL", ct);
        var varianceId = await db.Variances.AsNoTracking().Where(v => v.RunId == s.Run.Id && v.ScopeId == id).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);

        // The traceability chain: procedure -> recipe activities -> resources and the overhead
        // each activity received, with the rule and driver that sent it.
        var lines = await (from l in db.ProcedureActivityLines.AsNoTracking()
                           join d in db.CostingDefinitions on l.DefinitionId equals d.Id
                           join a in db.Activities on l.ActivityId equals a.Id
                           join c in db.CostCenters on a.CostCenterId equals c.Id
                           where d.ProcedureId == id && d.CostingVersionId == s.Version.Id
                           select new { a.Id, a.Name, l.Quantity, Cc = c.Name }).ToListAsync(ct);
        var actIds = lines.Select(l => l.Id).ToHashSet();
        var actCosts = await db.ActivityCosts.AsNoTracking().Where(a => a.RunId == s.Run.Id && actIds.Contains(a.ActivityId)).ToDictionaryAsync(a => a.ActivityId, ct);
        var allocs = await AllocationsAsync(s.Run.Id, a => a.TargetActivityId is { } t && actIds.Contains(t), ct);
        var chain = new List<ChainActivity>();
        foreach (var l in lines)
        {
            var detail = await ActivityAsync(s, p, l.Id, ct);
            actCosts.TryGetValue(l.Id, out var ac);
            chain.Add(new ChainActivity(l.Id, l.Name, l.Quantity, l.Cc, ac is null || ac.Volume == 0 ? 0 : Money.R4(ac.OverheadCost / ac.Volume),
                allocs.Where(a => a.TargetActivityId == l.Id).ToArray(), detail.Recipe));
        }

        var (start, end) = Runs.RunContextLoader.PeriodBounds(s.Period);
        var sample = await (from t in db.ActivityTransactions.AsNoTracking()
                            join sv in db.EncounterServices on t.EncounterServiceId equals sv.Id
                            join e in db.Encounters on t.EncounterId equals e.Id
                            join a in db.Activities on t.ActivityId equals a.Id
                            join r in db.Resources on t.ResourceId equals r.Id
                            where sv.ProcedureId == id && e.HospitalId == s.Hospital.Id && e.AdmitAt >= start && e.AdmitAt < end
                            orderby t.OccurredAt descending
                            select new SourceTxn(t.Id, e.EncounterNo, a.Name, r.Name, t.Quantity, t.Uom, t.OccurredAt, t.SourceSystem, t.SourceRef)).Take(12).ToArrayAsync(ct);

        // FR-11.9: the same procedure at every hospital in scope, same period, joined on catalogue code.
        var proc = await db.Procedures.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var across = (await (from pr in db.Procedures.AsNoTracking()
                             join r in db.CalculationRuns on pr.HospitalId equals r.HospitalId
                             join h in db.Hospitals on r.HospitalId equals h.Id
                             where pr.CatalogCode == proc.CatalogCode && r.PeriodId == s.Period.Id && r.RunType != "SIMULATION"
                             select new { pr.Id, HCode = h.Code, h.Name, RunCode = r.Code, RunId = r.Id, r.Status, r.CreatedAt }).ToListAsync(ct))
            .Where(x => p.HasHospital(x.HCode)).GroupBy(x => x.HCode).Select(g => g.OrderByDescending(x => x.Status == "PUBLISHED").ThenByDescending(x => x.CreatedAt).First()).ToList();
        var acrossRows = new List<CrossHospital>();
        foreach (var a in across)
        {
            var pcs = await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == a.RunId && c.ProcedureId == a.Id).ToListAsync(ct);
            var st = pcs.FirstOrDefault(c => c.CostType == "STANDARD");
            var ac = pcs.FirstOrDefault(c => c.CostType == "ACTUAL");
            acrossRows.Add(new CrossHospital(a.HCode, a.Name, a.RunCode, a.Status, a.Id, st?.UnitCost ?? 0, ac?.UnitCost ?? 0, ac?.Volume ?? 0));
        }

        return new ProcedureDetail(row, Components(p, comps), comps.Sum(c => c.Amount), actual,
            varianceId is { } vid ? await VarianceAsync(s, vid, ct) : null, chain.ToArray(), sample, acrossRows.ToArray(), Prov(s));
    }

    // ═══ Packages and DRGs ═══════════════════════════════════════════════════════════

    public sealed record PackageRow(Guid Id, string Code, string Name, string Description, decimal Price, int CoveredLOS, int Cases, decimal StandardCost,
        decimal ActualCost, decimal Revenue, decimal Variance, decimal VariancePct, decimal OperatingMargin, decimal MarginPct, decimal StandardPerCase, decimal ActualPerCase);
    public sealed record PackageComponentDto(Guid ProcedureId, string Procedure, decimal Quantity, decimal StandardUnit, decimal StandardLine);
    public sealed record PackageDetail(PackageRow Package, PackageComponentDto[] Components, EncounterRow[] Encounters, string Explanation);

    public async Task<PackageRow[]> PackagesAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var costs = await db.PackageCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id).ToDictionaryAsync(c => c.PackageId, ct);
        var showRevenue = p.Can("revenue.view");
        return (await db.Packages.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct)).Select(x =>
        {
            costs.TryGetValue(x.Id, out var c);
            var cases = c?.CaseCount ?? 0;
            return new PackageRow(x.Id, x.Code, x.Name, x.Description, x.Price, x.CoveredLOS, cases, c?.StandardCost ?? 0, c?.ActualCost ?? 0,
                showRevenue ? c?.Revenue ?? 0 : 0, c?.Variance ?? 0, c?.VariancePct ?? 0, showRevenue ? c?.OperatingMargin ?? 0 : 0, showRevenue ? c?.MarginPct ?? 0 : 0,
                cases == 0 ? 0 : Money.R4((c?.StandardCost ?? 0) / cases), cases == 0 ? 0 : Money.R4((c?.ActualCost ?? 0) / cases));
        }).ToArray();
    }

    public async Task<PackageDetail> PackageAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await PackagesAsync(s, p, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Package");
        var std = await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.CostType == "STANDARD").ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        var comps = (await (from c in db.PackageComponents.AsNoTracking()
                            join pr in db.Procedures on c.ProcedureId equals pr.Id
                            where c.PackageId == id
                            select new { pr.Id, pr.Name, c.Quantity }).ToListAsync(ct))
            .Select(x => new PackageComponentDto(x.Id, x.Name, x.Quantity, std.GetValueOrDefault(x.Id), Money.R4(std.GetValueOrDefault(x.Id) * x.Quantity))).ToArray();
        var encs = (await EncountersAsync(s, p, new EncounterQuery { PackageId = id, PageSize = 200 }, ct)).Rows;
        var perCase = row.Cases == 0 ? 0 : (row.ActualCost - row.StandardCost) / row.Cases;
        var explanation = row.Cases == 0 ? "No cases this period."
            : Math.Abs(perCase) < 1 ? "Actual cost is in line with the standard."
            : $"Each case ran SAR {Math.Abs(perCase):N0} {(perCase > 0 ? "above" : "below")} standard across {row.Cases} cases" +
              (p.Can("revenue.view") ? $"; the contracted price of SAR {row.Price:N0} {(row.ActualPerCase > row.Price ? "no longer covers" : "still covers")} the average case." : ".");
        return new PackageDetail(row, comps, encs, explanation);
    }

    public sealed record DrgRow(Guid Id, string Code, string Name, string Mdc, decimal RelativeWeight, decimal ExpectedLOS, int Cases, decimal Mean, decimal Median,
        decimal P25, decimal P75, decimal P90, decimal Min, decimal Max, decimal StdDev, decimal Cv, decimal AvgLOS, decimal CostPerDay, decimal AvgReimbursement,
        decimal AvgMargin, decimal MarginPct, int Outliers);
    public sealed record CompositionDiff(string ComponentType, string Label, decimal OutlierAverage, decimal OthersAverage, decimal Difference, decimal ShareOfGap);
    public sealed record DrgDetail(DrgRow Drg, decimal[] Costs, decimal OutlierFence, CompositionDiff[] WhyExpensive, EncounterRow[] Outliers, string Explanation);

    public async Task<DrgRow[]> DrgsAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var sums = await db.DrgCostSummaries.AsNoTracking().Where(d => d.RunId == s.Run.Id).ToDictionaryAsync(d => d.DrgId, ct);
        var showRevenue = p.Can("revenue.view");
        return (await db.Drgs.AsNoTracking().OrderBy(d => d.Code).ToListAsync(ct)).Select(d =>
        {
            sums.TryGetValue(d.Id, out var x);
            return new DrgRow(d.Id, d.Code, d.Name, d.Mdc, d.RelativeWt, d.ExpectedLOS, x?.CaseCount ?? 0, x?.AvgCost ?? 0, x?.MedianCost ?? 0, x?.P25Cost ?? 0,
                x?.P75Cost ?? 0, x?.P90Cost ?? 0, x?.MinCost ?? 0, x?.MaxCost ?? 0, x?.StdDevCost ?? 0, x?.VariationCoeff ?? 0, x?.AvgLOS ?? 0, x?.CostPerDay ?? 0,
                showRevenue ? x?.AvgReimbursement ?? 0 : 0, showRevenue ? x?.AvgMargin ?? 0 : 0, showRevenue ? x?.MarginPct ?? 0 : 0, x?.OutlierCount ?? 0);
        }).ToArray();
    }

    public async Task<DrgDetail> DrgAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await DrgsAsync(s, p, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("DRG");
        var encs = (await EncountersAsync(s, p, new EncounterQuery { DrgId = id, PageSize = 2000, Sort = "total" }, ct)).Rows;
        var fence = Money.R4(row.P75 + 1.5m * (row.P75 - row.P25));
        var outlierIds = encs.Where(e => e.TotalCost > fence).Select(e => e.Id).ToHashSet();
        // FR-5.14: why the expensive cases are expensive — average composition, outliers vs the rest.
        var comps = await db.CostComponents.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.Scope == "ENCOUNTER" && c.EncounterId != null)
            .Join(db.Encounters.Where(e => e.DrgId == id), c => c.EncounterId, e => e.Id, (c, e) => new { e.Id, c.ComponentType, c.Amount }).ToListAsync(ct);
        var nOut = Math.Max(outlierIds.Count, 1);
        var nRest = Math.Max(encs.Length - outlierIds.Count, 1);
        var diff = comps.GroupBy(c => c.ComponentType).Select(g =>
        {
            var o = g.Where(x => outlierIds.Contains(x.Id)).Sum(x => x.Amount) / nOut;
            var r = g.Where(x => !outlierIds.Contains(x.Id)).Sum(x => x.Amount) / nRest;
            return (Type: g.Key, O: Money.R4(o), R: Money.R4(r), D: Money.R4(o - r));
        }).OrderByDescending(x => x.D).ToList();
        var gap = diff.Where(d => d.D > 0).Sum(d => d.D);
        var whys = diff.Select(d => new CompositionDiff(d.Type, Catalogue.ComponentLabel(d.Type), d.O, d.R, d.D, gap == 0 ? 0 : Share(Math.Max(d.D, 0), gap))).ToArray();
        var top = whys.Where(w => w.Difference > 0).Take(2).ToList();
        var explanation = outlierIds.Count == 0
            ? "No case sits above the Tukey fence (P75 + 1.5 x IQR) this period."
            : $"{outlierIds.Count} high-cost {Fmt(outlierIds.Count)} above SAR {fence:N0}. They cost SAR {gap:N0} more on average than the rest, " +
              $"mostly {string.Join(" and ", top.Select(t => $"{t.Label.ToLowerInvariant()} (SAR {t.Difference:N0}, {t.ShareOfGap:N0}% of the gap)"))}.";
        return new DrgDetail(row, encs.Select(e => e.TotalCost).OrderBy(x => x).ToArray(), fence, whys,
            encs.Where(e => outlierIds.Contains(e.Id)).OrderByDescending(e => e.TotalCost).ToArray(), explanation);
        static string Fmt(int n) => n == 1 ? "case sits" : "cases sit";
    }
}

