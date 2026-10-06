using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
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

    public sealed record HospitalDto(Guid Id, string Code, string Name, string City, string Currency, int BedCount);
    public sealed record PeriodDto(Guid Id, string Code, DateOnly StartDate, DateOnly EndDate, int Days, string Status);
    public sealed record RunListItem(Guid Id, string Code, string Name, string Hospital, string HospitalName, string Period, string CostingVersion,
        string Status, string RunType, bool Forced, DateTime? StartedAt, DateTime? FinishedAt, int? DurationMs, string TriggeredBy,
        string? ApprovedBy, DateTime? PublishedAt, string? ErrorMessage, JsonElement? Metrics);
    public sealed record ContextDto(HospitalDto[] Hospitals, PeriodDto[] Periods, RunListItem[] Runs);

    public async Task<ContextDto> ContextAsync(Principal p, CancellationToken ct)
    {
        var hospitals = (await db.Hospitals.AsNoTracking().OrderBy(h => h.Code).ToListAsync(ct)).Where(h => p.HasHospital(h.Code))
            .Select(h => new HospitalDto(h.Id, h.Code, h.Name, h.City, h.Currency, h.BedCount)).ToArray();
        var periods = await db.Periods.AsNoTracking().OrderBy(x => x.StartDate)
            .Select(x => new PeriodDto(x.Id, x.Code, x.StartDate, x.EndDate, x.Days, x.Status)).ToArrayAsync(ct);
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
                          select new { r, h.Code, HName = h.Name, Period = pe.Code, Version = v.Code }).ToListAsync(ct);
        return rows.Where(x => p.HasHospital(x.Code)).Select(x => new RunListItem(x.r.Id, x.r.Code, x.r.Name, x.Code, x.HName, x.Period, x.Version,
            x.r.Status, x.r.RunType, x.r.Forced, x.r.StartedAt, x.r.FinishedAt, x.r.DurationMs, x.r.TriggeredBy, x.r.ApprovedBy, x.r.PublishedAt,
            x.r.ErrorMessage, x.r.MetricsJson is null ? null : JsonDocument.Parse(x.r.MetricsJson).RootElement)).ToArray();
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
                       s.Run.RunType, s.Run.Forced, s.Run.StartedAt, s.Run.FinishedAt, s.Run.DurationMs, s.Run.TriggeredBy, s.Run.ApprovedBy, s.Run.PublishedAt, s.Run.ErrorMessage, null);
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
    public sealed record DashboardDto(Tile[] Headline, Bar[] ServiceLines, Bar[] Procedures, Bar[] Variances, Bar[] Payers,
        Bar[] SupplyCost, Bar[] Waste, Bar[] Utilisation, Bar[] Residuals, Bar[] CostNature, string? Narrative);

    public async Task<DashboardDto> DashboardAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var rid = s.Run.Id;
        var rec = await db.Reconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == rid, ct);
        var enc = await EncounterFacts(rid, ct);
        var cost = enc.Sum(e => e.Total);
        var revenue = enc.Sum(e => e.Revenue);
        var direct = enc.Sum(e => e.Direct);
        var indirect = enc.Sum(e => e.Indirect);
        var overhead = enc.Sum(e => e.Overhead);
        var residuals = await db.ResidualBuckets.AsNoTracking().Where(b => b.RunId == rid).ToListAsync(ct);
        var idle = residuals.Where(b => b.Bucket == "UNUSED_CAPACITY").Sum(b => b.Amount);
        var canRevenue = p.Can("revenue.view");

        var tiles = new List<Tile>
        {
            new("gl", "General ledger", rec?.GlTotal ?? 0m, "SAR", "neutral", "reconciliation", "Operating expenditure posted to the period"),
            new("patientCost", "Cost attributed to patients", cost, "SAR", "lower-better", "patient-costing", $"{enc.Count:N0} encounters"),
            new("attribution", "Attribution", rec?.AttributionPct ?? 0m, "%", "neutral", "reconciliation", "Share of the ledger that reached a patient — comparable across hospitals"),
            new("idle", "Cost of unused capacity", idle, "SAR", "lower-better", "capacity", "Paid for, not consumed; never charged to a patient"),
            new("unexplained", "Unexplained", rec?.Unexplained ?? 0m, "SAR", "lower-better", "reconciliation", rec is null ? null : $"Materiality SAR {rec.Materiality:N2}"),
            new("perEncounter", "Average cost per encounter", enc.Count == 0 ? 0m : Money.R4(cost / enc.Count), "SAR", "lower-better", "patient-costing", null),
        };
        if (canRevenue)
        {
            tiles.Insert(2, new("revenue", "Net revenue", revenue, "SAR", "higher-better", "profitability", null));
            tiles.Insert(3, new("operatingMargin", "Operating margin", Money.R4(revenue - cost), "SAR", "higher-better", "profitability", $"{Share(revenue - cost, revenue)}% of revenue"));
            tiles.Insert(4, new("fullCostMargin", "Full-cost margin", Money.R4(revenue - cost - idle), "SAR", "higher-better", "profitability", "Operating margin less idle capacity (FR-5.17)"));
        }

        var sl = await db.ServiceLines.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var bySl = enc.GroupBy(e => e.ServiceLineId).Select(g => (Id: g.Key, Margin: g.Sum(e => e.Revenue - e.Total), Cost: g.Sum(e => e.Total)))
            .OrderByDescending(x => canRevenue ? x.Margin : x.Cost).ToList();
        var slMax = bySl.Count == 0 ? 0m : bySl.Max(x => Math.Abs(canRevenue ? x.Margin : x.Cost));

        var procs = await (from pc in db.ProcedureCosts.AsNoTracking()
                           join pr in db.Procedures on pc.ProcedureId equals pr.Id
                           where pc.RunId == rid && pc.CostType == "ACTUAL"
                           orderby pc.TotalCost descending
                           select new { pr.Id, pr.Name, pc.TotalCost, pc.Volume }).Take(8).ToListAsync(ct);
        var variances = await db.Variances.AsNoTracking().Where(v => v.RunId == rid).OrderByDescending(v => v.VarianceAmount).Take(6).ToListAsync(ct);

        var payers = await db.Payers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var byPayer = canRevenue
            ? enc.GroupBy(e => e.PayerId).Select(g => (Id: g.Key, Margin: g.Sum(e => e.Revenue - e.Total), Rev: g.Sum(e => e.Revenue))).OrderByDescending(x => x.Rev).ToList()
            : [];

        var units = await (from u in db.ResourceUnitCosts.AsNoTracking()
                           join r in db.Resources on u.ResourceId equals r.Id
                           where u.RunId == rid
                           select new { r.Id, r.Name, r.ResourceType, r.IsCapacityDriven, u.CostOfUsedCapacity, u.CostOfUnusedCapacity, u.UtilisationPct, u.AvailableMinutes }).ToListAsync(ct);
        var supply = units.Where(u => u.ResourceType is "DRUG" or "CONSUMABLE" or "IMPLANT" or "BLOOD").GroupBy(u => u.ResourceType)
            .Select(g => (Type: g.Key, Used: g.Sum(x => x.CostOfUsedCapacity), Waste: g.Sum(x => x.CostOfUnusedCapacity))).ToList();
        var utilisation = units.Where(u => u.IsCapacityDriven && u.AvailableMinutes > 0).GroupBy(u => u.ResourceType)
            .Select(g => (Type: g.Key, Util: Money.R4(g.Sum(x => x.CostOfUsedCapacity) / Math.Max(0.0001m, g.Sum(x => x.CostOfUsedCapacity + x.CostOfUnusedCapacity)) * 100m))).ToList();

        var resTotal = residuals.Sum(r => Math.Abs(r.Amount));
        return new DashboardDto(
            tiles.ToArray(),
            bySl.Take(8).Select(x => new Bar(x.Id?.ToString() ?? "none", x.Id is { } i ? sl.GetValueOrDefault(i, "Unassigned") : "Unassigned",
                canRevenue ? x.Margin : x.Cost, slMax == 0 ? 0 : Share(Math.Abs(canRevenue ? x.Margin : x.Cost), slMax), "profitability?dimension=serviceLine", null)).ToArray(),
            procs.Select(x => new Bar(x.Id.ToString(), x.Name, x.TotalCost, procs.Count == 0 ? 0 : Share(x.TotalCost, procs[0].TotalCost), $"procedures/{x.Id}", $"{x.Volume:N0} cases")).ToArray(),
            variances.Select(v => new Bar(v.Id.ToString(), v.ScopeLabel, v.VarianceAmount, variances.Count == 0 ? 0 : Share(Math.Abs(v.VarianceAmount), Math.Abs(variances[0].VarianceAmount)), $"variance/{v.Id}", v.Direction)).ToArray(),
            byPayer.Select(x => new Bar(x.Id.ToString(), payers.GetValueOrDefault(x.Id, "?"), x.Margin, Share(x.Rev, byPayer.Max(y => y.Rev)), "profitability?dimension=payer", $"revenue SAR {x.Rev:N0}")).ToArray(),
            supply.Select(x => new Bar(x.Type, Catalogue.ComponentLabel(x.Type), x.Used, Share(x.Used, supply.Sum(y => y.Used)), "resources", null)).ToArray(),
            supply.Select(x => new Bar(x.Type, Catalogue.ComponentLabel(x.Type), x.Waste, Share(x.Waste, x.Used + x.Waste), "capacity", "share of purchased value not issued")).ToArray(),
            utilisation.Select(x => new Bar(x.Type, Catalogue.ComponentLabel(x.Type), x.Util, x.Util, "capacity", "% of practical capacity")).ToArray(),
            residuals.OrderByDescending(r => Math.Abs(r.Amount)).Select(r => new Bar(r.Bucket, r.Label, r.Amount, Share(Math.Abs(r.Amount), resTotal), "reconciliation", Catalogue.ResidualOwner[r.Bucket])).ToArray(),
            [new("DIRECT", "Direct", direct, Share(direct, cost), null, null), new("INDIRECT", "Indirect", indirect, Share(indirect, cost), null, null), new("OVERHEAD", "Overhead", overhead, Share(overhead, cost), null, null)],
            rec?.Narrative);
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
