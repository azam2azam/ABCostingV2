using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ Costing models (FR-10.5, FR-10.6) ═══════════════════════════════════════════

    public sealed record VersionDto(Guid Id, string Code, string Name, string Description, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
        DateTime? PublishedAt, int Definitions, int Runs, string Hospital);
    public sealed record DiffLine(string Procedure, string Kind, string Item, decimal? From, decimal? To);
    public sealed record VersionDiff(string FromVersion, string ToVersion, DiffLine[] Changes);
    public sealed record CostingModelsDto(VersionDto[] Versions, VersionDiff[] DirectDiffs, VersionDiff[] IndirectDiffs, ApprovalDto[] Approvals);
    public sealed record NewVersionBody(Guid FromVersionId, string Code, string Name, string? Description, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

    public async Task<CostingModelsDto> CostingModelsAsync(Principal p, string? hospital, CancellationToken ct)
    {
        var procs = await db.Procedures.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var typeCost = await db.ProcedureTypeCostings.AsNoTracking().ToListAsync(ct);
        var itemMargins = await db.ProcedureMargins.AsNoTracking().ToListAsync(ct);
        var overheads = await db.EncounterOverheads.AsNoTracking().ToListAsync(ct);
        var centres = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var scope = await RunScope.HospitalCodesInScope(db, p, ct);
        var hospitals = await db.Hospitals.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, ct);
        var hospitalCode = string.IsNullOrWhiteSpace(hospital) ? null : hospital.Trim();
        if (hospitalCode is not null && !scope.Contains(hospitalCode)) throw AppError.OutOfScope(hospitalCode);

        bool InScope(Guid hospitalId)
        {
            var code = hospitals.GetValueOrDefault(hospitalId);
            if (code is null || !scope.Contains(code)) return false;
            return hospitalCode is null || code == hospitalCode;
        }

        var versions = (await db.CostingVersions.AsNoTracking().OrderBy(v => v.EffectiveFrom).ToListAsync(ct)).Where(v => InScope(v.HospitalId)).ToList();
        var defCounts = (await db.CostingDefinitions.AsNoTracking()
                .Join(db.Procedures.AsNoTracking(), d => d.ProcedureId, x => x.Id, (d, x) => new { d.CostingVersionId, x.HospitalId })
                .GroupBy(x => new { x.CostingVersionId, x.HospitalId })
                .Select(g => new { g.Key.CostingVersionId, g.Key.HospitalId, N = g.Count() }).ToListAsync(ct))
            .Where(x => InScope(x.HospitalId)).GroupBy(x => x.CostingVersionId).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var runs = (await db.CalculationRuns.AsNoTracking().Where(r => r.RunType != "SIMULATION")
                .GroupBy(r => new { r.CostingVersionId, r.HospitalId })
                .Select(g => new { g.Key.CostingVersionId, g.Key.HospitalId, N = g.Count() }).ToListAsync(ct))
            .Where(x => InScope(x.HospitalId)).GroupBy(x => x.CostingVersionId).ToDictionary(g => g.Key, g => g.Sum(x => x.N));

        Dictionary<(string Proc, string Kind, string Item), decimal> Snapshot(Guid versionId, string stream)
        {
            var types = typeCost.Where(x => x.CostingVersionId == versionId && x.Stream == stream && InScope(x.HospitalId))
                .Select(x => ((x.ProcType, "Type", hospitals[x.HospitalId]), x.MarginPct));
            var items = itemMargins.Where(x => x.CostingVersionId == versionId && x.Stream == stream && procs.ContainsKey(x.ProcedureId)
                    && InScope(procs[x.ProcedureId].HospitalId))
                .Select(x =>
                {
                    var proc = procs[x.ProcedureId];
                    return ((proc.Name, "Item", proc.Code), x.MarginPct);
                });
            IEnumerable<((string Proc, string Kind, string Item), decimal Value)> enc = [];
            if (stream == CostingStreamIndirect)
            {
                enc = overheads.Where(x => x.CostingVersionId == versionId && InScope(x.HospitalId))
                    .SelectMany(x =>
                    {
                        var cc = centres.GetValueOrDefault(x.CostCenterId);
                        var proc = cc?.Code ?? hospitals[x.HospitalId];
                        var item = string.IsNullOrWhiteSpace(x.Description) ? (cc?.Name ?? proc) : x.Description;
                        return new[]
                        {
                            ((proc, "Encounter %", item), x.OverheadPct),
                            ((proc, "Encounter cap", item), x.OverheadAmount),
                        };
                    });
            }
            return types.Concat(items).Concat(enc)
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Sum(x => x.Item2));
        }

        VersionDiff[] Diffs(string stream)
        {
            var diffs = new List<VersionDiff>();
            foreach (var own in versions.GroupBy(v => v.HospitalId).Select(g => g.ToList()))
                for (var i = 1; i < own.Count; i++)
                {
                    var a = Snapshot(own[i - 1].Id, stream);
                    var b = Snapshot(own[i].Id, stream);
                    var changes = a.Keys.Union(b.Keys)
                        .Select(k => new DiffLine(k.Proc, k.Kind, k.Item, a.TryGetValue(k, out var x) ? x : null, b.TryGetValue(k, out var y) ? y : null))
                        .Where(d => d.From != d.To).OrderBy(d => d.Kind).ThenBy(d => d.Procedure).ThenBy(d => d.Item).ToArray();
                    diffs.Add(new VersionDiff(own[i - 1].Code, own[i].Code, changes));
                }
            return diffs.ToArray();
        }

        return new CostingModelsDto(
            versions.Select(v => new VersionDto(v.Id, v.Code, v.Name, v.Description, v.EffectiveFrom, v.EffectiveTo, v.Status, v.PublishedAt,
                defCounts.GetValueOrDefault(v.Id), runs.GetValueOrDefault(v.Id), hospitals.GetValueOrDefault(v.HospitalId) ?? "")).ToArray(),
            Diffs(CostingStreamDirect), Diffs(CostingStreamIndirect),
            (await ApprovalsAsync(null, ct)).Where(a => a.EntityType == "COSTING_VERSION" && versions.Any(v => v.Id == a.EntityId)).ToArray());
    }

    public async Task<CostingModelsDto> CreateCostingVersionAsync(Principal p, NewVersionBody body, CancellationToken ct)
    {
        var code = (body.Code ?? "").Trim();
        var name = (body.Name ?? "").Trim();
        if (code.Length == 0) throw AppError.Invalid("Version code is required.");
        if (name.Length == 0) name = code;
        var from = await db.CostingVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == body.FromVersionId, ct)
            ?? throw AppError.NotFound("Source costing version");
        var hospitalCode = await db.Hospitals.AsNoTracking().Where(h => h.Id == from.HospitalId).Select(h => h.Code).FirstAsync(ct);
        if (!p.HasHospital(hospitalCode)) throw AppError.OutOfScope(hospitalCode);
        if (await db.CostingVersions.AnyAsync(v => v.HospitalId == from.HospitalId && v.Code == code, ct))
            throw AppError.Conflict("VERSION_EXISTS", $"{hospitalCode} already has a costing version named {code}.");
        if (body.EffectiveTo is { } to && to < body.EffectiveFrom)
            throw AppError.Invalid("effectiveTo is before effectiveFrom.");

        var version = new CostingVersion
        {
            Id = IdGen.Sequential(), HospitalId = from.HospitalId, Code = code, Name = name, Description = (body.Description ?? "").Trim(),
            EffectiveFrom = body.EffectiveFrom, EffectiveTo = body.EffectiveTo, Status = "DRAFT", CreatedAt = DateTime.UtcNow,
        };
        db.CostingVersions.Add(version);

        var defs = await db.CostingDefinitions.AsNoTracking().Where(d => d.CostingVersionId == from.Id).ToListAsync(ct);
        var defMap = defs.ToDictionary(d => d.Id, _ => IdGen.Sequential());
        db.CostingDefinitions.AddRange(defs.Select(d => new CostingDefinition
        {
            Id = defMap[d.Id], ProcedureId = d.ProcedureId, CostingVersionId = version.Id, Notes = d.Notes,
        }));
        var defIds = defs.Select(d => d.Id).ToList();
        if (defIds.Count > 0)
        {
            var actLines = await db.ProcedureActivityLines.AsNoTracking().Where(l => defIds.Contains(l.DefinitionId)).ToListAsync(ct);
            var resLines = await db.ProcedureResourceLines.AsNoTracking().Where(l => defIds.Contains(l.DefinitionId)).ToListAsync(ct);
            db.ProcedureActivityLines.AddRange(actLines.Select(l => new ProcedureActivityLine
            {
                Id = IdGen.Sequential(), DefinitionId = defMap[l.DefinitionId], ActivityId = l.ActivityId, Quantity = l.Quantity,
            }));
            db.ProcedureResourceLines.AddRange(resLines.Select(l => new ProcedureResourceLine
            {
                Id = IdGen.Sequential(), DefinitionId = defMap[l.DefinitionId], ResourceId = l.ResourceId, Quantity = l.Quantity, Uom = l.Uom,
            }));
        }

        var types = await db.ProcedureTypeCostings.AsNoTracking().Where(x => x.CostingVersionId == from.Id).ToListAsync(ct);
        db.ProcedureTypeCostings.AddRange(types.Select(x => new ProcedureTypeCosting
        {
            Id = IdGen.Sequential(), HospitalId = x.HospitalId, CostingVersionId = version.Id,
            ProcType = x.ProcType, Stream = x.Stream, MarginPct = x.MarginPct,
        }));
        var items = await db.ProcedureMargins.AsNoTracking().Where(x => x.CostingVersionId == from.Id).ToListAsync(ct);
        db.ProcedureMargins.AddRange(items.Select(x => new ProcedureMargin
        {
            Id = IdGen.Sequential(), HospitalId = x.HospitalId, CostingVersionId = version.Id,
            ProcedureId = x.ProcedureId, Stream = x.Stream, MarginPct = x.MarginPct,
        }));
        var overheads = await db.EncounterOverheads.AsNoTracking().Where(x => x.CostingVersionId == from.Id).ToListAsync(ct);
        db.EncounterOverheads.AddRange(overheads.Select(x => new EncounterOverhead
        {
            Id = IdGen.Sequential(), HospitalId = x.HospitalId, CostingVersionId = version.Id, CostCenterId = x.CostCenterId,
            Description = x.Description, OverheadPct = x.OverheadPct, OverheadAmount = x.OverheadAmount,
        }));

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "CREATE", "CostingVersion", version.Id.ToString(), version.Code,
            after: new { source = from.Code, code = version.Code, version.Name, version.EffectiveFrom,
                copied = new { definitions = defs.Count, types = types.Count, items = items.Count, overheads = overheads.Count } }, ct: ct);
        return await CostingModelsAsync(p, hospitalCode, ct);
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
