using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    public sealed record AllocationDto(Guid Id, int Stage, string? Rule, string Source, string Target, string TargetType, Guid? TargetCostCentreId,
        Guid? TargetActivityId, string? Driver, decimal DriverQty, decimal DriverVolume, decimal DriverShare, decimal Amount, int Iteration, string CalcExpression);

    private async Task<List<AllocationDto>> AllocationsAsync(Guid runId, Func<AllocationResult, bool> filter, CancellationToken ct)
    {
        var rows = (await db.AllocationResults.AsNoTracking().Where(a => a.RunId == runId).ToListAsync(ct)).Where(filter).ToList();
        var rules = await db.AllocationRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.OrderBy(a => a.Stage).ThenByDescending(a => a.Amount).Select(a => new AllocationDto(a.Id, a.Stage,
            a.RuleId is { } r ? rules.GetValueOrDefault(r) : null, a.SourceCostCenterId is { } s ? ccs.GetValueOrDefault(s, "?") : "?", a.TargetLabel, a.TargetType,
            a.TargetCostCenterId, a.TargetActivityId, a.DriverId is { } d ? drivers.GetValueOrDefault(d) : null, a.DriverQty, a.DriverVolume, a.DriverShare,
            a.Amount, a.Iteration, a.CalcExpression)).ToList();
    }

    private bool DeptAllowed(Principal p, Dictionary<Guid, string> deptCodes, Guid deptId) =>
        p.AllDepartments || (deptCodes.TryGetValue(deptId, out var code) && p.HasDepartment(code));

    // ═══ Cost centres ════════════════════════════════════════════════════════════════

    public sealed record CostCentreRow(Guid Id, string Code, string Name, string CcType, int StepDownSeq, string Department, string? Facility,
        decimal Direct, decimal Received, decimal AllocatedOut, decimal Final, bool IsClosed, decimal GlPosted);
    public sealed record CostCentreDetail(CostCentreRow CostCentre, AllocationDto[] Inbound, AllocationDto[] Outbound, PoolRow[] Pools, ActivityRow[] Activities);

    public async Task<CostCentreRow[]> CostCentresAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var facs = await db.Facilities.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var bal = await db.CostCenterBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id).ToDictionaryAsync(b => b.CostCenterId, ct);
        var gl = await db.GLPostings.AsNoTracking().Where(g => g.HospitalId == s.Hospital.Id && g.PeriodId == s.Period.Id)
            .GroupBy(g => g.CostCenterId).Select(g => new { g.Key, A = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.A, ct);
        return (await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(c => DeptAllowed(p, deptCodes, c.DepartmentId))
            .OrderBy(c => c.StepDownSeq).ThenBy(c => c.Code)
            .Select(c =>
            {
                bal.TryGetValue(c.Id, out var b);
                return new CostCentreRow(c.Id, c.Code, c.Name, c.CcType, c.StepDownSeq, depts[c.DepartmentId].Name, c.FacilityId is { } f ? facs.GetValueOrDefault(f) : null,
                    b?.DirectAmount ?? 0, b?.ReceivedAmount ?? 0, b?.AllocatedOut ?? 0, b?.FinalAmount ?? 0, b?.IsClosed ?? false, gl.GetValueOrDefault(c.Id));
            }).ToArray();
    }

    public async Task<CostCentreDetail> CostCentreAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await CostCentresAsync(s, p, ct)).FirstOrDefault(c => c.Id == id) ?? throw AppError.NotFound("Cost centre");
        var allocs = await AllocationsAsync(s.Run.Id, a => a.TargetCostCenterId == id || a.SourceCostCenterId == id, ct);
        var pools = (await CostPoolsAsync(s, ct)).Where(x => x.CostCentreId == id).ToArray();
        var acts = (await ActivitiesAsync(s, p, ct)).Where(a => a.CostCentreId == id).ToArray();
        return new CostCentreDetail(row,
            allocs.Where(a => a.TargetCostCentreId == id && a.TargetType == "COST_CENTER").ToArray(),
            allocs.Where(a => a.Source == row.Name && !(a.TargetCostCentreId == id && a.TargetType == "COST_CENTER")).ToArray(), pools, acts);
    }

    // ═══ Cost pools ══════════════════════════════════════════════════════════════════

    public sealed record PoolRow(Guid Id, string Code, string Name, string? Description, Guid CostCentreId, string CostCentre, string Classification,
        string AllocMethod, string Stream, string? PrimaryDriver, string Status, int Version, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal DirectAmount, int Resources);
    public sealed record MappingDto(string Account, string NaturalClass, string CostCentre, decimal Weight, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
    public sealed record PoolDetail(PoolRow Pool, MappingDto[] Mappings, RuleRow[] Rules, AllocationDto[] Allocations, ResourceRow[] FundedResources);

    public async Task<PoolRow[]> CostPoolsAsync(ScopedRun s, CancellationToken ct)
    {
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var funded = await db.Resources.AsNoTracking().Where(r => r.CostPoolId != null).GroupBy(r => r.CostPoolId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var bal = await db.CostPoolBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id).ToDictionaryAsync(b => b.CostPoolId, b => b.DirectAmount, ct);
        return (await db.CostPools.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => new PoolRow(x.Id, x.Code, x.Name, x.Description, x.CostCenterId, ccs.GetValueOrDefault(x.CostCenterId, "?"), x.Classification, x.AllocMethod,
                // CR-4: derived, never flagged by hand.
                funded.ContainsKey(x.Id) ? "CONSUMPTION" : "ALLOCATION", x.PrimaryDriverId is { } d ? drivers.GetValueOrDefault(d) : null, x.Status, x.Version,
                x.EffectiveFrom, x.EffectiveTo, bal.GetValueOrDefault(x.Id), funded.GetValueOrDefault(x.Id))).ToArray();
    }

    public async Task<PoolDetail> CostPoolAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var pool = (await CostPoolsAsync(s, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Cost pool");
        var maps = await (from m in db.GLMappings.AsNoTracking()
                          join a in db.GLAccounts on m.GLAccountId equals a.Id
                          join c in db.CostCenters on m.CostCenterId equals c.Id
                          where m.CostPoolId == id
                          select new MappingDto($"{a.Code} {a.Name}", a.NaturalClass, c.Name, m.Weight, m.EffectiveFrom, m.EffectiveTo)).ToArrayAsync(ct);
        var rules = (await AllocationRulesAsync(s, ct)).Where(r => r.SourcePoolId == id).ToArray();
        var ruleIds = rules.Select(r => r.Id).ToHashSet();
        var allocs = await AllocationsAsync(s.Run.Id, a => a.SourcePoolId == id || (a.RuleId is { } r && ruleIds.Contains(r)), ct);
        var resources = (await ResourcesAsync(s, p, ct)).Where(r => r.CostPoolId == id).ToArray();
        return new PoolDetail(pool, maps, rules, allocs.ToArray(), resources);
    }

    // ═══ Resources ═══════════════════════════════════════════════════════════════════

    public sealed record ResourceRow(Guid Id, string Code, string Name, string ResourceType, string Nature, string Uom, bool IsCapacityDriven, string CostCentre,
        Guid? CostPoolId, string? CostPool, string? Rate, decimal UtilisationPct, decimal CostOfUsed, decimal CostOfUnused, decimal UsedQuantity);
    public sealed record RateDto(string? UnitCost, string Uom, string Basis, string SourceSystem, DateOnly EffectiveFrom, DateOnly? EffectiveTo, int Version, string Status, bool EffectiveInRun);
    public sealed record CapacityDto2(decimal TheoreticalMinutes, decimal PracticalRatio, decimal PracticalMinutes, string? AnnualCost, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
    public sealed record ResourceDetail(ResourceRow Resource, RateDto[] Rates, CapacityDto2[] Capacity, CapacityRow? UnitCost, string[] Providers, RecipeUse[] UsedBy);
    public sealed record RecipeUse(Guid ActivityId, string Activity, decimal StdQuantity, string Uom);

    public async Task<ResourceRow[]> ResourcesAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var pools = await db.CostPools.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var units = await db.ResourceUnitCosts.AsNoTracking().Where(u => u.RunId == s.Run.Id).ToDictionaryAsync(u => u.ResourceId, ct);
        return (await db.Resources.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).OrderBy(r => r.ResourceType).ThenBy(r => r.Code).ToListAsync(ct))
            .Select(r =>
            {
                units.TryGetValue(r.Id, out var u);
                return new ResourceRow(r.Id, r.Code, r.Name, r.ResourceType, Catalogue.NatureOf(r.ResourceType), r.Uom, r.IsCapacityDriven, ccs.GetValueOrDefault(r.CostCenterId, "?"),
                    r.CostPoolId, r.CostPoolId is { } pid ? pools.GetValueOrDefault(pid) : null, u is null ? null : Masked(p, r.ResourceType, u.UnitCost),
                    u?.UtilisationPct ?? 0, u?.CostOfUsedCapacity ?? 0, u?.CostOfUnusedCapacity ?? 0, u?.UsedMinutes ?? 0);
            }).ToArray();
    }

    public async Task<ResourceDetail> ResourceAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await ResourcesAsync(s, p, ct)).FirstOrDefault(r => r.Id == id) ?? throw AppError.NotFound("Resource");
        var asOf = s.Period.EndDate;
        var rates = (await db.ResourceRates.AsNoTracking().Where(r => r.ResourceId == id).OrderBy(r => r.EffectiveFrom).ToListAsync(ct))
            .Select(r => new RateDto(Masked(p, row.ResourceType, r.UnitCost), r.Uom, r.Basis, r.SourceSystem, r.EffectiveFrom, r.EffectiveTo, r.Version, r.Status, r.IsEffective(asOf))).ToArray();
        var caps = (await db.ResourceCapacities.AsNoTracking().Where(c => c.ResourceId == id).OrderBy(c => c.EffectiveFrom).ToListAsync(ct))
            .Select(c => new CapacityDto2(c.TheoreticalMinutes, c.PracticalRatio, c.PracticalMinutes, row.ResourceType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : c.AnnualCost.ToString("0.0000"), c.EffectiveFrom, c.EffectiveTo)).ToArray();
        var unit = (await CapacityAsync(s, p, ct)).Rows.FirstOrDefault(r => r.ResourceId == id);
        var providers = await db.Providers.AsNoTracking().Where(x => x.ResourceId == id).OrderBy(x => x.Name).Select(x => $"{x.Name} — {x.Grade}, {x.Specialty}").ToArrayAsync(ct);
        var used = await (from ar in db.ActivityResources.AsNoTracking()
                          join a in db.Activities on ar.ActivityId equals a.Id
                          where ar.ResourceId == id
                          select new RecipeUse(a.Id, a.Name, ar.StdQuantity, ar.Uom)).ToArrayAsync(ct);
        return new ResourceDetail(row, rates, caps, unit, providers, used);
    }

    // ═══ Cost drivers ════════════════════════════════════════════════════════════════

    public sealed record DriverRow(Guid Id, string Code, string CatalogCode, string Name, string Description, string Unit, string DriverType,
        string SourceSystem, string SourceField, string Formula, string Frequency, string AllocMethodology, string Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal Volume);
    public sealed record DriverVolumeDto(string CostCentre, decimal Quantity, decimal Share, string SourceSystem);
    public sealed record DriverDetail(DriverRow Driver, DriverVolumeDto[] Volumes, RuleRow[] Rules);

    public async Task<DriverRow[]> DriversAsync(ScopedRun s, CancellationToken ct)
    {
        var vols = await db.DriverVolumes.AsNoTracking().Where(v => v.PeriodId == s.Period.Id).GroupBy(v => v.DriverId)
            .Select(g => new { g.Key, Q = g.Sum(v => v.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        return (await db.CostDrivers.AsNoTracking().Where(d => d.HospitalId == s.Hospital.Id).OrderBy(d => d.Code).ToListAsync(ct))
            .Select(d => new DriverRow(d.Id, d.Code, d.CatalogCode, d.Name, d.Description, d.Unit, d.DriverType, d.SourceSystem, d.SourceField, d.Formula,
                d.Frequency, d.AllocMethodology, d.Status, d.EffectiveFrom, d.EffectiveTo, vols.GetValueOrDefault(d.Id))).ToArray();
    }

    public async Task<DriverDetail> DriverAsync(ScopedRun s, Guid id, CancellationToken ct)
    {
        var row = (await DriversAsync(s, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("Cost driver");
        var vols = await (from v in db.DriverVolumes.AsNoTracking()
                          join c in db.CostCenters on v.CostCenterId equals c.Id
                          where v.DriverId == id && v.PeriodId == s.Period.Id
                          select new { c.Name, v.Quantity, v.SourceSystem }).ToListAsync(ct);
        var total = vols.Sum(v => v.Quantity);
        return new DriverDetail(row, vols.OrderByDescending(v => v.Quantity).Select(v => new DriverVolumeDto(v.Name, v.Quantity, Share(v.Quantity, total), v.SourceSystem)).ToArray(),
            (await AllocationRulesAsync(s, ct)).Where(r => r.DriverId == id).ToArray());
    }

    // ═══ Activities ══════════════════════════════════════════════════════════════════

    public sealed record ActivityRow(Guid Id, string Code, string CatalogCode, string Name, string Category, Guid CostCentreId, string CostCentre, string Department,
        decimal StdDuration, decimal MinDuration, decimal MaxDuration, string Variability, string Directness, decimal Volume, decimal DirectCost,
        decimal OverheadCost, decimal TotalCost, decimal UnitCost, string? CalcExpression, string State);
    public sealed record RecipeLineDto(Guid ResourceId, string Resource, string ResourceType, decimal StdQuantity, string Uom, string? Rate, decimal? LineCost);
    public sealed record ActivityDetail(ActivityRow Activity, RecipeLineDto[] Recipe, AllocationDto[] OverheadIn, ProcedureUse[] UsedBy);
    public sealed record ProcedureUse(Guid ProcedureId, string Procedure, decimal Quantity);

    public async Task<ActivityRow[]> ActivitiesAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var costs = await db.ActivityCosts.AsNoTracking().Where(a => a.RunId == s.Run.Id).ToDictionaryAsync(a => a.ActivityId, ct);
        var closed = await db.CostCenterBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id && b.IsClosed).Select(b => b.CostCenterId).ToListAsync(ct);
        return (await db.Activities.AsNoTracking().Where(a => a.HospitalId == s.Hospital.Id).OrderBy(a => a.Code).ToListAsync(ct))
            .Where(a => DeptAllowed(p, deptCodes, a.DepartmentId))
            .Select(a =>
            {
                costs.TryGetValue(a.Id, out var c);
                // FR-11.14: degraded states are named, never rendered as an empty row.
                var state = c is not null ? "COSTED"
                    : closed.Contains(a.CostCenterId) ? "FULLY_ALLOCATED_SUPPORT"
                    : "NO_CASES";
                return new ActivityRow(a.Id, a.Code, a.CatalogCode, a.Name, a.Category, a.CostCenterId, ccs[a.CostCenterId].Name, depts[a.DepartmentId].Name,
                    a.StdDuration, a.MinDuration, a.MaxDuration, a.Variability, a.Directness, c?.Volume ?? 0, c?.DirectCost ?? 0, c?.OverheadCost ?? 0,
                    c?.TotalCost ?? 0, c?.UnitCost ?? 0, c?.CalcExpression, state);
            }).ToArray();
    }

    public async Task<ActivityDetail> ActivityAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await ActivitiesAsync(s, p, ct)).FirstOrDefault(a => a.Id == id) ?? throw AppError.NotFound("Activity");
        var units = await db.ResourceUnitCosts.AsNoTracking().Where(u => u.RunId == s.Run.Id).ToDictionaryAsync(u => u.ResourceId, u => u.UnitCost, ct);
        var recipe = (await (from ar in db.ActivityResources.AsNoTracking()
                             join r in db.Resources on ar.ResourceId equals r.Id
                             where ar.ActivityId == id
                             select new { r.Id, r.Name, r.ResourceType, ar.StdQuantity, ar.Uom }).ToListAsync(ct))
            .Select(x =>
            {
                var hasRate = units.TryGetValue(x.Id, out var rate);
                var masked = Masked(p, x.ResourceType, rate);
                return new RecipeLineDto(x.Id, x.Name, x.ResourceType, x.StdQuantity, x.Uom, hasRate ? masked : null, hasRate && masked is not null ? Money.R4(x.StdQuantity * rate) : null);
            }).ToArray();
        var allocs = await AllocationsAsync(s.Run.Id, a => a.TargetActivityId == id, ct);
        var uses = await (from l in db.ProcedureActivityLines.AsNoTracking()
                          join d in db.CostingDefinitions on l.DefinitionId equals d.Id
                          join pr in db.Procedures on d.ProcedureId equals pr.Id
                          where l.ActivityId == id && d.CostingVersionId == s.Version.Id
                          select new ProcedureUse(pr.Id, pr.Name, l.Quantity)).ToArrayAsync(ct);
        return new ActivityDetail(row, recipe, allocs.ToArray(), uses);
    }

    // ═══ Allocation rules ════════════════════════════════════════════════════════════

    public sealed record RuleTargetDto(string? CostCentre, decimal? Percentage, decimal Weight);
    public sealed record RuleRow(Guid Id, string Code, string Name, Guid SourcePoolId, string SourcePool, string SourceCostCentre, Guid? DriverId, string? Driver,
        string Method, string Methodology, int Stage, int Sequence, decimal PoolShare, string? Formula, string Status, int Version, DateOnly EffectiveFrom,
        DateOnly? EffectiveTo, decimal MovedThisRun, int Results, RuleTargetDto[] Targets);

    public async Task<RuleRow[]> AllocationRulesAsync(ScopedRun s, CancellationToken ct)
    {
        var pools = await db.CostPools.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).ToDictionaryAsync(x => x.Id, ct);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var moved = await db.AllocationResults.AsNoTracking().Where(a => a.RunId == s.Run.Id && a.RuleId != null).GroupBy(a => a.RuleId!.Value)
            .Select(g => new { g.Key, A = g.Sum(x => x.Amount), N = g.Count() }).ToDictionaryAsync(x => x.Key, ct);
        var targets = (await db.AllocationRuleTargets.AsNoTracking().ToListAsync(ct)).ToLookup(t => t.RuleId);
        return (await db.AllocationRules.AsNoTracking().ToListAsync(ct)).Where(r => pools.ContainsKey(r.SourcePoolId))
            .OrderBy(r => r.Stage).ThenBy(r => r.Sequence).ThenBy(r => r.Code)
            .Select(r =>
            {
                moved.TryGetValue(r.Id, out var m);
                var pool = pools[r.SourcePoolId];
                return new RuleRow(r.Id, r.Code, r.Name, r.SourcePoolId, pool.Name, ccs.GetValueOrDefault(pool.CostCenterId, "?"), r.DriverId,
                    r.DriverId is { } d ? drivers.GetValueOrDefault(d) : null, r.Method, r.Methodology, r.Stage, r.Sequence, r.PoolShare, r.Formula, r.Status,
                    r.Version, r.EffectiveFrom, r.EffectiveTo, m?.A ?? 0, m?.N ?? 0,
                    targets[r.Id].Select(t => new RuleTargetDto(t.CostCenterId is { } c ? ccs.GetValueOrDefault(c) : null, t.Percentage, t.Weight)).ToArray());
            }).ToArray();
    }
}
