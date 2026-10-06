using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ Costing models (FR-10.5, FR-10.6) ═══════════════════════════════════════════

    public sealed record VersionDto(Guid Id, string Code, string Name, string Description, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
        DateTime? PublishedAt, int Definitions, int Runs);
    public sealed record DiffLine(string Procedure, string Kind, string Item, decimal? From, decimal? To);
    public sealed record VersionDiff(string FromVersion, string ToVersion, DiffLine[] Changes);
    public sealed record CostingModelsDto(VersionDto[] Versions, VersionDiff[] Diffs, ApprovalDto[] Approvals);

    public async Task<CostingModelsDto> CostingModelsAsync(Principal p, CancellationToken ct)
    {
        var versions = await db.CostingVersions.AsNoTracking().OrderBy(v => v.EffectiveFrom).ToListAsync(ct);
        var defs = await db.CostingDefinitions.AsNoTracking().ToListAsync(ct);
        var runs = await db.CalculationRuns.AsNoTracking().Where(r => r.RunType != "SIMULATION").GroupBy(r => r.CostingVersionId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var procs = await db.Procedures.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var acts = await db.Activities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var res = await db.Resources.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var actLines = (await db.ProcedureActivityLines.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.DefinitionId);
        var resLines = (await db.ProcedureResourceLines.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.DefinitionId);
        var scope = await RunScope.HospitalCodesInScope(db, p, ct);
        var hospitals = await db.Hospitals.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, ct);

        // Each version's recipes keyed by (procedure catalogue code at a hospital, item), so the
        // computed diff says which quantity changed, from what to what — a diff, not a description.
        Dictionary<(string Proc, string Kind, string Item), decimal> Recipe(Guid versionId) =>
            defs.Where(d => d.CostingVersionId == versionId && procs.ContainsKey(d.ProcedureId) && scope.Contains(hospitals[procs[d.ProcedureId].HospitalId]))
                .SelectMany(d =>
                {
                    var pname = $"{procs[d.ProcedureId].Name} ({hospitals[procs[d.ProcedureId].HospitalId]})";
                    return actLines[d.Id].Select(l => ((pname, "Activity", acts.GetValueOrDefault(l.ActivityId, "?")), l.Quantity))
                        .Concat(resLines[d.Id].Select(l => ((pname, "Resource", res.GetValueOrDefault(l.ResourceId, "?")), l.Quantity)));
                })
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var diffs = new List<VersionDiff>();
        for (var i = 1; i < versions.Count; i++)
        {
            var a = Recipe(versions[i - 1].Id);
            var b = Recipe(versions[i].Id);
            var changes = a.Keys.Union(b.Keys)
                .Select(k => new DiffLine(k.Proc, k.Kind, k.Item, a.TryGetValue(k, out var x) ? x : null, b.TryGetValue(k, out var y) ? y : null))
                .Where(d => d.From != d.To).OrderBy(d => d.Procedure).ThenBy(d => d.Item).ToArray();
            diffs.Add(new VersionDiff(versions[i - 1].Code, versions[i].Code, changes));
        }
        return new CostingModelsDto(
            versions.Select(v => new VersionDto(v.Id, v.Code, v.Name, v.Description, v.EffectiveFrom, v.EffectiveTo, v.Status, v.PublishedAt,
                defs.Count(d => d.CostingVersionId == v.Id), runs.GetValueOrDefault(v.Id))).ToArray(),
            diffs.ToArray(), (await ApprovalsAsync(null, ct)).Where(a => a.EntityType == "COSTING_VERSION").ToArray());
    }

    // ═══ Scenarios (FR-8) ════════════════════════════════════════════════════════════

    public sealed record LeverDto(string LeverType, string LeverLabel, string TargetType, string? TargetId, string TargetLabel, string ChangeType, decimal Value, string? Note, string Description);
    public sealed record ScenarioResultDto(string Dimension, string? DimensionId, string Label, decimal BaselineCost, decimal ScenarioCost, decimal CostDelta,
        decimal CostDeltaPct, decimal BaselineRevenue, decimal ScenarioRevenue, decimal RevenueDelta, decimal BaselineMargin, decimal ScenarioMargin, decimal MarginDelta, string Explanation);
    public sealed record ScenarioDto(Guid Id, string Code, string Name, string Description, Guid BaseRunId, string BaseRun, string Hospital, string Period,
        string Status, string CreatedBy, DateTime CreatedAt, DateTime? ExecutedAt, LeverDto[] Levers, ScenarioResultDto[] Results);

    public static string DescribeLever(string leverType, string targetLabel, string changeType, decimal value)
    {
        var label = Catalogue.LeverLabel.GetValueOrDefault(leverType, leverType);
        var change = changeType switch
        {
            "PCT" => $"{(value > 0 ? "+" : "")}{value:0.##}%",
            "SET" => $"set to {value:0.####}x",
            _ => $"{(value > 0 ? "+" : "")}{value:0.####}",
        };
        return $"{label} — {targetLabel}: {change}";
    }

    public async Task<ScenarioDto[]> ScenariosAsync(Principal p, Guid? id, CancellationToken ct)
    {
        var rows = await (from s in db.Scenarios.AsNoTracking()
                          join r in db.CalculationRuns on s.BaseRunId equals r.Id
                          join h in db.Hospitals on r.HospitalId equals h.Id
                          join pe in db.Periods on r.PeriodId equals pe.Id
                          where id == null || s.Id == id
                          orderby s.CreatedAt
                          select new { s, Run = r.Code, H = h.Code, P = pe.Code }).ToListAsync(ct);
        rows = rows.Where(x => p.HasHospital(x.H)).ToList();
        var ids = rows.Select(x => x.s.Id).ToList();
        var levers = (await db.ScenarioLevers.AsNoTracking().Where(l => ids.Contains(l.ScenarioId)).ToListAsync(ct)).ToLookup(l => l.ScenarioId);
        var results = (await db.ScenarioResults.AsNoTracking().Where(r => ids.Contains(r.ScenarioId)).ToListAsync(ct)).ToLookup(r => r.ScenarioId);
        var showRevenue = p.Can("revenue.view");
        string[] dimOrder = ["TOTAL", "COMPONENT_TYPE", "PROCEDURE"];
        return rows.Select(x => new ScenarioDto(x.s.Id, x.s.Code, x.s.Name, x.s.Description, x.s.BaseRunId, x.Run, x.H, x.P, x.s.Status, x.s.CreatedBy,
            x.s.CreatedAt, x.s.ExecutedAt,
            levers[x.s.Id].Select(l => new LeverDto(l.LeverType, Catalogue.LeverLabel.GetValueOrDefault(l.LeverType, l.LeverType), l.TargetType, l.TargetId,
                l.TargetLabel, l.ChangeType, l.Value, l.Note, DescribeLever(l.LeverType, l.TargetLabel, l.ChangeType, l.Value))).ToArray(),
            results[x.s.Id].OrderBy(r => Array.IndexOf(dimOrder, r.Dimension)).ThenByDescending(r => Math.Abs(r.CostDelta))
                .Select(r => new ScenarioResultDto(r.Dimension, r.DimensionId, r.Label, r.BaselineCost, r.ScenarioCost, r.CostDelta, r.CostDeltaPct,
                    showRevenue ? r.BaselineRevenue : 0, showRevenue ? r.ScenarioRevenue : 0, showRevenue ? r.RevenueDelta : 0,
                    showRevenue ? r.BaselineMargin : 0, showRevenue ? r.ScenarioMargin : 0, showRevenue ? r.MarginDelta : 0, r.Explanation)).ToArray())).ToArray();
    }

    // ═══ Audit (FR-10.13) and administration ═════════════════════════════════════════

    public sealed record AuditDto(Guid Id, DateTimeOffset At, string ActorEmail, string Action, string EntityType, string? EntityId, string? EntityLabel,
        string? BeforeJson, string? AfterJson, string? Metadata, string? IpAddress);
    public sealed record AuditPage(AuditDto[] Rows, int Total, int Page, int PageSize, string[] Actions, string[] EntityTypes);

    public async Task<AuditPage> AuditAsync(string? action, string? entityType, string? actor, int page, int pageSize, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.ActorEmail.Contains(actor));
        var total = await q.CountAsync(ct);
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(page, 1);
        var rows = await q.OrderByDescending(a => a.At).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditDto(a.Id, a.At, a.ActorEmail, a.Action, a.EntityType, a.EntityId, a.EntityLabel, a.BeforeJson, a.AfterJson, a.Metadata, a.IpAddress))
            .ToArrayAsync(ct);
        var types = await db.AuditLogs.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        return new AuditPage(rows, total, page, pageSize, Catalogue.AuditActions, types);
    }

    public sealed record UserDto(Guid Id, string Email, string Name, string Role, string RoleName, string Title, string HospitalScope, string DeptScope,
        bool IsActive, DateTime? LastLoginAt);
    public sealed record GovernanceDto(ApprovalDto[] Requests, ApprovalDto[] ControlFailures, object Flows);

    public async Task<UserDto[]> UsersAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct))
            .Select(u => new UserDto(u.Id, u.Email, u.Name, u.Role, Security.Role(u.Role)?.Name ?? u.Role, u.Title, u.HospitalScope, u.DeptScope, u.IsActive, u.LastLoginAt)).ToArray();

    public async Task<GovernanceDto> GovernanceAsync(CancellationToken ct)
    {
        var all = await ApprovalsAsync(null, ct);
        // FR-10.4: a request where requester and reviewer are the same actor is a control failure.
        return new GovernanceDto(all, all.Where(a => a.SelfApproved).ToArray(), Catalogue.ApprovalFlow);
    }
}
