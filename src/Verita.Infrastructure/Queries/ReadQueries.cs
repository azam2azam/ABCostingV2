using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Queries;

/// <summary>
/// The read models behind the interface and the API (one surface, FR-11 / ARCH-11). Profitability
/// and other group-bys are computed at read time over a run's encounter costs (FR-5.16); nothing
/// here aggregates raw transactions except the explicit drill-down to source (FR-11.8).
/// </summary>
public sealed partial class ReadQueries(VeritaDbContext db, IAuditWriter audit)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task<ScopedRun> Scope(Principal p, string? run, string? hospital, string? period, CancellationToken ct) =>
        RunScope.ResolveAsync(db, p, run, hospital, period, ct);

    private static decimal Share(decimal part, decimal whole) => whole == 0m ? 0m : Money.R4(part / whole * 100m);

    /// <summary>FR-0.9: labour rates and costs are masked without salary.view.</summary>
    private static string? Masked(Principal p, string resourceType, decimal value) =>
        resourceType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : Money.R6(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ═══ Context, runs ═══════════════════════════════════════════════════════════════

    public sealed record HospitalDto(Guid Id, string Code, string Name, string City, string Currency, int BedCount, string? HisSetupId, int? HisProjectId, bool HasDrgs, bool UsesCostingModel);
    public sealed record PeriodDto(Guid Id, string Code, DateOnly StartDate, DateOnly EndDate, int Days, string Status, string? Hospital);
    public sealed record RunListItem(Guid Id, string Code, string Name, string Hospital, string HospitalName, string Period, string CostingVersion,
        string Status, string RunType, bool Forced, DateTime? StartedAt, DateTime? FinishedAt, int? DurationMs, string TriggeredBy,
        string? ApprovedBy, DateTime? PublishedAt, string? ErrorMessage, JsonElement? Metrics, string? CurrentStep, string CostingVersionStatus);
    public sealed record ContextDto(HospitalDto[] Hospitals, PeriodDto[] Periods, RunListItem[] Runs);

    public async Task<ContextDto> ContextAsync(Principal p, CancellationToken ct)
    {
        var withDrg = (await db.Encounters.AsNoTracking().Where(e => e.DrgId != null).Select(e => e.HospitalId).Distinct().ToListAsync(ct)).ToHashSet();
        var withModel = (await db.ProcedureTypeCostings.AsNoTracking().Where(x => x.Stream != CostingStreamPackageDirect && x.Stream != CostingStreamZeroFixed).Select(x => x.HospitalId).Distinct().ToListAsync(ct))
            .Concat(await db.ProcedureMargins.AsNoTracking().Where(x => x.Stream != CostingStreamPackageDirect && x.Stream != CostingStreamZeroFixed).Select(x => x.HospitalId).Distinct().ToListAsync(ct))
            .Concat(await db.EncounterOverheads.AsNoTracking().Select(x => x.HospitalId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var hospitals = (await db.Hospitals.AsNoTracking().OrderBy(h => h.Code).ToListAsync(ct)).Where(h => p.HasHospital(h.Code))
            .Select(h => new HospitalDto(h.Id, h.Code, h.Name, h.City, h.Currency, h.BedCount, h.HisSetupId, h.HisProjectId, withDrg.Contains(h.Id), withModel.Contains(h.Id))).ToArray();
        var periodRows = await (from x in db.Periods.AsNoTracking()
                                join h in db.Hospitals.AsNoTracking() on x.HospitalId equals h.Id into hh
                                from h in hh.DefaultIfEmpty()
                                orderby x.StartDate, x.Code
                                select new { x, Hospital = h.Code }).ToListAsync(ct);
        var periods = periodRows.Select(x => new PeriodDto(x.x.Id, x.x.Code, x.x.StartDate, x.x.EndDate, x.x.Days, x.x.Status, x.Hospital)).ToArray();
        return new ContextDto(hospitals, periods, await RunsAsync(p, ct));
    }

    public async Task<RunListItem[]> RunsAsync(Principal p, CancellationToken ct)
    {
        var rows = await (from r in db.CalculationRuns.AsNoTracking()
                          join h in db.Hospitals on r.HospitalId equals h.Id
                          join pe in db.Periods on r.PeriodId equals pe.Id
                          join v in db.CostingVersions on r.CostingVersionId equals v.Id
                          where r.RunType != "SIMULATION"
                          orderby pe.StartDate descending, h.Code
                          select new { r, h.Code, HName = h.Name, Period = pe.Code, Version = v.Code, VersionStatus = v.Status }).ToListAsync(ct);
        var running = await db.RunSteps.AsNoTracking().Where(s => s.Status == "RUNNING")
            .Select(s => new { s.RunId, s.StepCode, s.StepName }).ToListAsync(ct);
        var stepByRun = running.GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.First());
        return rows.Where(x => p.HasHospital(x.Code)).Select(x =>
        {
            var step = stepByRun.GetValueOrDefault(x.r.Id);
            return new RunListItem(x.r.Id, x.r.Code, x.r.Name, x.Code, x.HName, x.Period, x.Version,
                x.r.Status, x.r.RunType, x.r.Forced, x.r.StartedAt, x.r.FinishedAt, x.r.DurationMs, x.r.TriggeredBy, x.r.ApprovedBy, x.r.PublishedAt,
                x.r.ErrorMessage, x.r.MetricsJson is null ? null : JsonDocument.Parse(x.r.MetricsJson).RootElement, step?.StepCode, x.VersionStatus);
        }).ToArray();
    }

    public sealed record StepDto(int StepNo, string StepCode, string StepName, string Status, int? DurationMs, int RowsWritten,
        decimal MoneyIn, decimal MoneyOut, decimal Gap, bool MovesMoney, string? Message, JsonElement? Metrics);
    public sealed record ApprovalDto(Guid Id, string EntityType, Guid EntityId, string EntityLabel, string Stage, string RequestedBy, string? ReviewedBy,
        string RequestNote, string? ReviewNote, DateTime RequestedAt, DateTime? ReviewedAt, bool SelfApproved);
    public sealed record RunDetail(RunListItem Run, StepDto[] Steps, string[] PublishBlockers, int[] DqCounts, ApprovalDto[] Approvals);

    public async Task<RunDetail> RunDetailAsync(ScopedRun s, Principal p, Func<VeritaDbContext, CalculationRun, Task<List<string>>> blockers, CancellationToken ct)
    {
        var item = (await RunsAsync(p, ct)).FirstOrDefault(r => r.Id == s.Run.Id)
                   ?? new RunListItem(s.Run.Id, s.Run.Code, s.Run.Name, s.Hospital.Code, s.Hospital.Name, s.Period.Code, s.Version.Code, s.Run.Status,
                       s.Run.RunType, s.Run.Forced, s.Run.StartedAt, s.Run.FinishedAt, s.Run.DurationMs, s.Run.TriggeredBy, s.Run.ApprovedBy, s.Run.PublishedAt, s.Run.ErrorMessage, null, null, s.Version.Status);
        var steps = await db.RunSteps.AsNoTracking().Where(x => x.RunId == s.Run.Id).OrderBy(x => x.StepNo).ToListAsync(ct);
        var defs = Catalogue.RunSteps.ToDictionary(d => d.Code);
        var dq = await db.DataQualityIssues.AsNoTracking().Where(i => i.RunId == s.Run.Id).GroupBy(i => i.Severity).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var counts = Catalogue.Severities.Select(sv => dq.FirstOrDefault(d => d.Key == sv)?.N ?? 0).ToArray();
        return new RunDetail(item,
            steps.Select(x => new StepDto(x.StepNo, x.StepCode, x.StepName, x.Status, x.DurationMs, x.RowsWritten, x.MoneyIn, x.MoneyOut,
                Money.R4(x.MoneyIn - x.MoneyOut), defs.TryGetValue(x.StepCode, out var d) && d.MovesMoney, x.Message,
                x.MetricsJson is null ? null : JsonDocument.Parse(x.MetricsJson).RootElement)).ToArray(),
            (await blockers(db, s.Run)).ToArray(), counts, await ApprovalsAsync(s.Run.Id, ct));
    }

    public async Task<ApprovalDto[]> ApprovalsAsync(Guid? entityId, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        var q = db.ApprovalRequests.AsNoTracking();
        if (entityId is { } id) q = q.Where(a => a.EntityId == id);
        return (await q.OrderByDescending(a => a.RequestedAt).ToListAsync(ct)).Select(a => new ApprovalDto(a.Id, a.EntityType, a.EntityId, a.EntityLabel, a.Stage,
            users.GetValueOrDefault(a.RequestedById, "?"), a.ReviewedById is { } r ? users.GetValueOrDefault(r, "?") : null, a.RequestNote, a.ReviewNote,
            a.RequestedAt, a.ReviewedAt, a.ReviewedById == a.RequestedById)).ToArray();
    }

    // ═══ Dashboard (FR-11.5) ═════════════════════════════════════════════════════════

    public sealed record Tile(string Key, string Label, decimal Value, string Unit, string Direction, string? Link, string? Note);
    public sealed record Bar(string Key, string Label, decimal Value, decimal Share, string? Link, string? Note);
    public sealed record DashboardDto(Tile[] Headline, Bar[] CostNature);

    public async Task<DashboardDto> DashboardAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var (direct, indirect, overhead, revenue, encounters) = await RunTotalsAsync(s.Run, ct);
        var cost = Money.R4(direct + indirect + overhead);
        var tiles = new List<Tile>
        {
            new("encounters", "Encounters", encounters, "count", "neutral", "patient-encounters", "Total no. of encounters"),
            new("direct", "Direct cost", direct, "SAR", "lower-better", "procedure-costing", "Contracted price × Margin Cost %"),
            new("indirect", "Indirect cost", indirect, "SAR", "lower-better", "indirect-costing", "Contracted price × Overhead %"),
            new("overheads", "Overheads", overhead, "SAR", "lower-better", "indirect-costing", "Encounter level overheads"),
        };
        if (p.Can("revenue.view"))
        {
            tiles.Add(new("revenue", "Revenue", revenue, "SAR", "higher-better", null, $"{encounters:N0} encounters"));
        }
        tiles.Add(new("total", "Total cost", cost, "SAR", "lower-better", null, "Direct + Indirect + Overheads"));
        if (p.Can("revenue.view"))
        {
            tiles.Add(new("margin", "Margin", Money.R4(revenue - cost), "SAR", "higher-better", null, $"{Share(revenue - cost, revenue):N1}% of revenue"));
        }
        return new DashboardDto(tiles.ToArray(),
            [new("DIRECT", "Direct cost", direct, Share(direct, cost), "procedure-costing", null), new("INDIRECT", "Indirect cost", indirect, Share(indirect, cost), "indirect-costing", null), new("OVERHEAD", "Overheads", overhead, Share(overhead, cost), "indirect-costing", null)]);
    }

    /// <summary>A run's whole totals: what a costing-model run stored, or the sum of an engine run's encounters.</summary>
    private async Task<(decimal Direct, decimal Indirect, decimal Overhead, decimal Revenue, int Encounters)> RunTotalsAsync(CalculationRun run, CancellationToken ct)
    {
        var rid = run.Id;
        if (run.CostingMethod == CostingModelPipeline.Method)
        {
            var whole = await db.RunDimensionCosts.AsNoTracking().FirstOrDefaultAsync(x => x.RunId == rid && x.Dimension == "hospital", ct);
            return (whole?.DirectCost ?? 0m, whole?.IndirectCost ?? 0m, whole?.OverheadCost ?? 0m, whole?.Revenue ?? 0m, whole?.Encounters ?? 0);
        }
        var sum = await db.EncounterCosts.AsNoTracking().Where(x => x.RunId == rid).GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DirectCost), I = g.Sum(x => x.IndirectCost), O = g.Sum(x => x.OverheadCost), R = g.Sum(x => x.Revenue), N = g.Count() })
            .FirstOrDefaultAsync(ct);
        return (sum?.D ?? 0m, sum?.I ?? 0m, sum?.O ?? 0m, sum?.R ?? 0m, sum?.N ?? 0);
    }

    // Encounter facts used by dashboards, profitability and reports.
    private sealed record EncFact(Guid Id, Guid HospitalId, Guid DepartmentId, Guid? ServiceLineId, Guid PayerId, Guid? DrgId, Guid? PackageId,
        Guid? ProviderId, string EncType, string Segment, decimal Direct, decimal Indirect, decimal Overhead, decimal Total, decimal Revenue, decimal Standard);

    private async Task<List<EncFact>> EncounterFacts(Guid runId, CancellationToken ct) =>
        await (from ec in db.EncounterCosts.AsNoTracking()
               join e in db.Encounters on ec.EncounterId equals e.Id
               join pt in db.Patients on e.PatientId equals pt.Id
               where ec.RunId == runId
               select new EncFact(e.Id, e.HospitalId, e.DepartmentId, e.ServiceLineId, e.PayerId, e.DrgId, e.PackageId, e.ProviderId, e.EncType, pt.Segment,
                   ec.DirectCost, ec.IndirectCost, ec.OverheadCost, ec.TotalCost, ec.Revenue, ec.StandardCost)).ToListAsync(ct);
}
