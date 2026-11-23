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

    /// <summary>
    /// One run's figures: the whole hospital, one HIS episode type (<paramref name="encType"/>: IP, OP, ER) or one
    /// care area (<paramref name="careArea"/>: IPD, OR, OPD, ER). Care area wins when both are given.
    /// </summary>
    public async Task<DashboardDto> DashboardAsync(ScopedRun s, Principal p, CancellationToken ct, string? encType = null, string? careArea = null)
    {
        var slice = Slice(encType, careArea);
        var (direct, indirect, overhead, revenue, encounters) = slice is null
            ? await RunTotalsAsync(s.Run, ct)
            : await RunTotalsForSliceAsync(s.Run.Id, slice.Value, ct);
        var cost = Money.R4(direct + indirect + overhead);
        var scope = slice is null ? "episodes" : $"{slice.Value.Key} episodes";
        var requests = await AdmissionRequestsByAreaAsync(s, ct);
        var tiles = new List<Tile>
        {
            new("encounters", "Episodes", encounters, "count", "neutral", "patient-encounters", $"Total no. of {scope}"),
            new("admissionRequests", "Admission requests", requests.Where(x => InSlice(slice, x.Key)).Sum(x => x.Value), "count", "neutral", null, "Raised in the period, not cancelled"),
            new("direct", "Direct cost", direct, "SAR", "lower-better", "procedure-costing", "Contracted price × Margin Cost %"),
            new("indirect", "Indirect cost", indirect, "SAR", "lower-better", "indirect-costing", "Contracted price × Overhead %"),
            new("overheads", "Overheads", overhead, "SAR", "lower-better", "indirect-costing", "Episode level overheads"),
        };
        if (p.Can("revenue.view"))
        {
            tiles.Add(new("revenue", "Revenue", revenue, "SAR", "higher-better", null, $"{encounters:N0} {scope}"));
        }
        tiles.Add(new("total", "Total cost", cost, "SAR", "lower-better", null, "Direct + Indirect + Overheads"));
        if (p.Can("revenue.view"))
        {
            tiles.Add(new("margin", "Margin", Money.R4(revenue - cost), "SAR", "higher-better", null, $"{Share(revenue - cost, revenue):N1}% of revenue"));
        }
        return new DashboardDto(tiles.ToArray(),
            [new("DIRECT", "Direct cost", direct, Share(direct, cost), "procedure-costing", null), new("INDIRECT", "Indirect cost", indirect, Share(indirect, cost), "indirect-costing", null), new("OVERHEAD", "Overheads", overhead, Share(overhead, cost), "indirect-costing", null)]);
    }

    /// <summary>One run's IPD / OR / OPD / ER split, in report order. The four add up to the run.</summary>
    public sealed record CareAreaRow(string Key, string Label, int Encounters, decimal Revenue, decimal TotalCost, decimal Margin, decimal MarginPct, decimal RevenueShare,
        decimal Direct = 0m, decimal Indirect = 0m, decimal Overhead = 0m, int AdmissionRequests = 0);
    public sealed record CareAreaSplitDto(CareAreaRow[] Areas);

    public async Task<CareAreaSplitDto> CareAreaSplitAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var showRevenue = p.Can("revenue.view");
        var totals = await CareAreaTotalsAsync(s.Run.Id, ct);
        var requests = await AdmissionRequestsByAreaAsync(s, ct);
        var allRevenue = totals.Values.Sum(x => x.Revenue);
        var rows = CareArea.All.Select(area =>
        {
            var (direct, indirect, overhead, revenue, n) = totals.GetValueOrDefault(area);
            var cost = Money.R4(direct + indirect + overhead);
            revenue = showRevenue ? Money.R4(revenue) : 0m;
            var margin = showRevenue ? Money.R4(revenue - cost) : 0m;
            return new CareAreaRow(area, CareArea.Label(area), n, revenue, cost, margin,
                showRevenue && revenue != 0m ? Money.R4(margin / revenue * 100m) : 0m, showRevenue ? Share(revenue, allRevenue) : 0m,
                Money.R4(direct), Money.R4(indirect), Money.R4(overhead), requests.GetValueOrDefault(area));
        }).ToArray();
        return new CareAreaSplitDto(rows);
    }

    /// <summary>One point per costed period for the hospital — Summary month-by-month charts.</summary>
    public sealed record TrendPoint(string Period, DateOnly StartDate, DateOnly EndDate, int Encounters,
        decimal Revenue, decimal TotalCost, decimal Margin, decimal MarginPct,
        int IpdEncounters = 0, int OrEncounters = 0, int OpdEncounters = 0, int ErEncounters = 0);
    public sealed record DashboardTrendDto(TrendPoint[] Months);

    public async Task<DashboardTrendDto> DashboardTrendAsync(ScopedRun s, Principal p, CancellationToken ct, string? encType = null, string? careArea = null)
    {
        var showRevenue = p.Can("revenue.view");
        var slice = Slice(encType, careArea);
        var owned = await db.Periods.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.StartDate).ToListAsync(ct);
        // Month by month: custom periods (e.g. Ramadan) overlap the months and would count their days twice.
        var periods = owned.Where(x => !PeriodCalendar.IsCustom(x, owned)).ToList();
        var runs = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.HospitalId == s.Hospital.Id && r.RunType != "SIMULATION"
                        && (r.Status == "RECONCILED" || r.Status == "PUBLISHED"))
            .ToListAsync(ct);
        var best = runs.GroupBy(r => r.PeriodId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.FinishedAt ?? r.StartedAt ?? DateTime.MinValue).First());
        var runIds = best.Values.Select(r => r.Id).ToList();
        // Prepared by AGGREGATE (hospital, encounterType, careArea). Re-run periods to fill older runs.
        var dims = runIds.Count == 0 ? [] : await db.RunDimensionCosts.AsNoTracking()
            .Where(x => runIds.Contains(x.RunId) && (x.Dimension == "hospital" || x.Dimension == "encounterType" || x.Dimension == "careArea"))
            .ToListAsync(ct);
        var byRun = dims.GroupBy(x => x.RunId).ToDictionary(g => g.Key, g => g.ToDictionary(x => (x.Dimension, x.Key)));
        var months = new List<TrendPoint>();
        foreach (var period in periods)
        {
            if (!best.TryGetValue(period.Id, out var run)) continue;
            var stored = byRun.GetValueOrDefault(run.Id) ?? [];
            var row = stored.GetValueOrDefault(slice ?? ("hospital", run.HospitalId.ToString()));
            if (slice is null) row ??= stored.Values.FirstOrDefault(x => x.Dimension == "hospital");
            // Runs from before AGGREGATE wrote these rows: add them up once until the period is re-run.
            var (direct, indirect, overhead, revenue, encounters) = row is not null
                ? (row.DirectCost, row.IndirectCost, row.OverheadCost, row.Revenue, row.Encounters)
                : slice is null
                    ? await RunTotalsAsync(run, ct)
                    : await RunTotalsForSliceAsync(run.Id, slice.Value, ct);
            var cost = Money.R4(direct + indirect + overhead);
            revenue = showRevenue ? Money.R4(revenue) : 0m;
            var margin = showRevenue ? Money.R4(revenue - cost) : 0m;
            var marginPct = showRevenue && revenue != 0m ? Money.R4(margin / revenue * 100m) : 0m;
            var areas = stored.Values.Any(x => x.Dimension == "careArea")
                ? stored.Values.Where(x => x.Dimension == "careArea").ToDictionary(x => x.Key, x => x.Encounters)
                : (await CareAreaTotalsAsync(run.Id, ct)).ToDictionary(x => x.Key, x => x.Value.Encounters);
            // A sliced trend counts only the care areas inside the slice.
            int AreaCount(string area) => InSlice(slice, area) ? areas.GetValueOrDefault(area) : 0;
            months.Add(new TrendPoint(period.Code, period.StartDate, period.EndDate, encounters, revenue, cost, margin, marginPct,
                AreaCount(CareArea.Ipd), AreaCount(CareArea.Or), AreaCount(CareArea.Opd), AreaCount(CareArea.Er)));
        }
        return new DashboardTrendDto(months.ToArray());
    }

    /// <summary>
    /// HIS admission requests raised in the selected period (by request date, so custom periods work too), leaving out
    /// cancelled ones (HIS status 4) and inactive ones. Each is in one care area, so the four add up: admitted as an OR
    /// episode → OR; admitted otherwise → IPD; not (yet) admitted → where it was raised, OPD or ER.
    /// </summary>
    private async Task<Dictionary<string, int>> AdmissionRequestsByAreaAsync(ScopedRun s, CancellationToken ct)
    {
        var hid = s.Hospital.Id;
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var rows = await (from r in db.AdmissionRequests.AsNoTracking()
                          where r.HospitalId == hid && r.RequestedAt >= start && r.RequestedAt < end
                                && (r.HisStatus == null || r.HisStatus != 4) && r.IsActive != false
                          join e in db.Encounters.AsNoTracking().Where(e => e.HospitalId == hid && e.EncType == "IP")
                              on r.AdmissionEncounterNo equals e.EncounterNo into ej
                          from e in ej.DefaultIfEmpty()
                          group r by new { r.Source, Admitted = r.AdmissionEncounterNo != null, Surgical = e != null && e.IsSurgical } into g
                          select new { g.Key.Source, g.Key.Admitted, g.Key.Surgical, N = g.Count() }).ToListAsync(ct);
        return rows.GroupBy(x => x.Admitted ? (x.Surgical ? CareArea.Or : CareArea.Ipd) : x.Source == CareArea.Er ? CareArea.Er : CareArea.Opd)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.N));
    }

    /// <summary>The stored dimension a dashboard reads: a care area first, else an HIS episode type, else the whole run.</summary>
    private static (string Dimension, string Key)? Slice(string? encType, string? careArea) =>
        !string.IsNullOrWhiteSpace(careArea) ? CareArea.IsKnown(careArea) ? ("careArea", careArea) : throw AppError.Invalid($"Unknown care area {careArea}. Use IPD, OR, OPD or ER.")
        : !string.IsNullOrWhiteSpace(encType) ? ("encounterType", encType)
        : null;

    private static bool InSlice((string Dimension, string Key)? slice, string area) => slice switch
    {
        null => true,
        ("careArea", var key) => key == area,
        (_, "IP") => area is CareArea.Ipd or CareArea.Or,
        (_, "ER") => area == CareArea.Er,
        (_, _) => area == CareArea.Opd,
    };

    /// <summary>Encounters in one care area, as an EF filter (the same rule as <see cref="CareArea.Of"/>).</summary>
    private static IQueryable<Encounter> InCareArea(IQueryable<Encounter> q, string area) => area switch
    {
        CareArea.Or => q.Where(e => e.EncType == "IP" && e.IsSurgical),
        CareArea.Ipd => q.Where(e => e.EncType == "IP" && !e.IsSurgical),
        CareArea.Er => q.Where(e => e.EncType == "ER"),
        _ => q.Where(e => e.EncType != "IP" && e.EncType != "ER"),
    };

    private async Task<(decimal Direct, decimal Indirect, decimal Overhead, decimal Revenue, int Encounters)> RunTotalsForSliceAsync(
        Guid runId, (string Dimension, string Key) slice, CancellationToken ct)
    {
        var (dimension, key) = slice;
        var stored = await db.RunDimensionCosts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RunId == runId && x.Dimension == dimension && x.Key == key, ct);
        if (stored is not null) return (stored.DirectCost, stored.IndirectCost, stored.OverheadCost, stored.Revenue, stored.Encounters);
        var encounters = db.Encounters.AsNoTracking();
        encounters = dimension == "careArea" ? InCareArea(encounters, key) : encounters.Where(e => e.EncType == key);
        var sum = await (from ec in db.EncounterCosts.AsNoTracking()
                         join e in encounters on ec.EncounterId equals e.Id
                         where ec.RunId == runId
                         group ec by 1 into g
                         select new { D = g.Sum(x => x.DirectCost), I = g.Sum(x => x.IndirectCost), O = g.Sum(x => x.OverheadCost),
                             R = g.Sum(x => x.Revenue), N = g.Count() }).FirstOrDefaultAsync(ct);
        return (sum?.D ?? 0m, sum?.I ?? 0m, sum?.O ?? 0m, sum?.R ?? 0m, sum?.N ?? 0);
    }

    /// <summary>A run's totals per care area: the stored careArea rows, else added up from its encounter costs.</summary>
    private async Task<Dictionary<string, (decimal Direct, decimal Indirect, decimal Overhead, decimal Revenue, int Encounters)>> CareAreaTotalsAsync(
        Guid runId, CancellationToken ct)
    {
        var stored = await db.RunDimensionCosts.AsNoTracking().Where(x => x.RunId == runId && x.Dimension == "careArea").ToListAsync(ct);
        if (stored.Count > 0)
            return stored.ToDictionary(x => x.Key, x => (x.DirectCost, x.IndirectCost, x.OverheadCost, x.Revenue, x.Encounters));
        var groups = await (from ec in db.EncounterCosts.AsNoTracking()
                            join e in db.Encounters.AsNoTracking() on ec.EncounterId equals e.Id
                            where ec.RunId == runId
                            group ec by new { e.EncType, e.IsSurgical } into g
                            select new { g.Key.EncType, g.Key.IsSurgical, D = g.Sum(x => x.DirectCost), I = g.Sum(x => x.IndirectCost),
                                O = g.Sum(x => x.OverheadCost), R = g.Sum(x => x.Revenue), N = g.Count() }).ToListAsync(ct);
        return groups.GroupBy(x => CareArea.Of(x.EncType, x.IsSurgical))
            .ToDictionary(g => g.Key, g => (g.Sum(x => x.D), g.Sum(x => x.I), g.Sum(x => x.O), g.Sum(x => x.R), g.Sum(x => x.N)));
    }

    /// <summary>A run's hospital totals from RunDimensionCost when present; else the sum of EncounterCost.</summary>
    private async Task<(decimal Direct, decimal Indirect, decimal Overhead, decimal Revenue, int Encounters)> RunTotalsAsync(CalculationRun run, CancellationToken ct)
    {
        var rid = run.Id;
        var whole = await db.RunDimensionCosts.AsNoTracking().FirstOrDefaultAsync(x => x.RunId == rid && x.Dimension == "hospital", ct);
        if (whole is not null) return (whole.DirectCost, whole.IndirectCost, whole.OverheadCost, whole.Revenue, whole.Encounters);
        var sum = await db.EncounterCosts.AsNoTracking().Where(x => x.RunId == rid).GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DirectCost), I = g.Sum(x => x.IndirectCost), O = g.Sum(x => x.OverheadCost), R = g.Sum(x => x.Revenue), N = g.Count() })
            .FirstOrDefaultAsync(ct);
        return (sum?.D ?? 0m, sum?.I ?? 0m, sum?.O ?? 0m, sum?.R ?? 0m, sum?.N ?? 0);
    }

    // Encounter facts used by dashboards, profitability and reports.
    private sealed record EncFact(Guid Id, Guid HospitalId, Guid DepartmentId, Guid? ServiceLineId, Guid PayerId, Guid? DrgId, Guid? PackageId,
        Guid? ProviderId, string EncType, string Segment, decimal Direct, decimal Indirect, decimal Overhead, decimal Total, decimal Revenue, decimal Standard,
        bool IsSurgical = false)
    {
        public string Area => CareArea.Of(EncType, IsSurgical);
    }

    private async Task<List<EncFact>> EncounterFacts(Guid runId, CancellationToken ct) =>
        await (from ec in db.EncounterCosts.AsNoTracking()
               join e in db.Encounters on ec.EncounterId equals e.Id
               join pt in db.Patients on e.PatientId equals pt.Id
               where ec.RunId == runId
               select new EncFact(e.Id, e.HospitalId, e.DepartmentId, e.ServiceLineId, e.PayerId, e.DrgId, e.PackageId, e.ProviderId, e.EncType, pt.Segment,
                   ec.DirectCost, ec.IndirectCost, ec.OverheadCost, ec.TotalCost, ec.Revenue, ec.StandardCost, e.IsSurgical)).ToListAsync(ct);
}
