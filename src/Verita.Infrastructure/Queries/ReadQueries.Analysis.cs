using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ Profitability (FR-5.15 – FR-5.17) ═══════════════════════════════════════════

    public static readonly string[] Dimensions =
        ["hospital", "department", "serviceLine", "physician", "payer", "procedure", "package", "drg", "encounterType", "segment"];

    public sealed record ProfitRow(string Key, string Label, int Encounters, decimal Volume, decimal Revenue, decimal Direct, decimal Indirect,
        decimal Overhead, decimal Total, decimal Contribution, decimal Operating, decimal MarginPct, string? Link);
    public sealed record ProfitDto(string Dimension, ProfitRow[] Rows, ProfitRow Total, decimal IdleCapacity, decimal FullCostMargin);

    public async Task<ProfitDto> ProfitabilityAsync(ScopedRun s, Principal p, string dimension, CancellationToken ct)
    {
        if (!Dimensions.Contains(dimension)) throw AppError.InvalidDimension(dimension, Dimensions);
        var showRevenue = p.Can("revenue.view");
        var rid = s.Run.Id;
        var idle = await db.ResidualBuckets.AsNoTracking().Where(b => b.RunId == rid && b.Bucket == "UNUSED_CAPACITY").SumAsync(b => (decimal?)b.Amount, ct) ?? 0m;
        List<ProfitRow> rows;

        ProfitRow Make(string key, string label, int n, decimal vol, decimal rev, decimal d, decimal i, decimal o, string? link)
        {
            var total = d + i + o;
            rev = showRevenue ? rev : 0m;
            return new ProfitRow(key, label, n, vol, rev, d, i, o, total, showRevenue ? rev - d - i : 0m, showRevenue ? rev - total : 0m,
                showRevenue && rev != 0m ? Money.R4((rev - total) / rev * 100m) : 0m, link);
        }

        if (dimension == "procedure")
        {
            rows = (await (from pc in db.ProcedureCosts.AsNoTracking()
                           join pr in db.Procedures on pc.ProcedureId equals pr.Id
                           where pc.RunId == rid && pc.CostType == "ACTUAL"
                           select new { pr.Id, pr.Name, pc.Volume, pc.Revenue, pc.DirectCost, pc.IndirectCost, pc.OverheadCost }).ToListAsync(ct))
                .Select(x => Make(x.Id.ToString(), x.Name, 0, x.Volume, x.Revenue, x.DirectCost, x.IndirectCost, x.OverheadCost, $"procedures/{x.Id}")).ToList();
        }
        else
        {
            var facts = await EncounterFacts(rid, ct);
            var labels = dimension switch
            {
                "hospital" => await db.Hospitals.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => x.Name, ct),
                "department" => await db.Departments.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => x.Name, ct),
                "serviceLine" => await db.ServiceLines.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => x.Name, ct),
                "physician" => await db.Providers.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => $"{x.Name} ({x.Specialty})", ct),
                "payer" => await db.Payers.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => x.Name, ct),
                "package" => await db.Packages.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => x.Name, ct),
                "drg" => await db.Drgs.AsNoTracking().ToDictionaryAsync(x => (Guid?)x.Id, x => $"{x.Code} {x.Name}", ct),
                _ => new Dictionary<Guid?, string>(),
            };
            Func<EncFact, string> key = dimension switch
            {
                "hospital" => f => f.HospitalId.ToString(),
                "department" => f => f.DepartmentId.ToString(),
                "serviceLine" => f => f.ServiceLineId?.ToString() ?? "",
                "physician" => f => f.ProviderId?.ToString() ?? "",
                "payer" => f => f.PayerId.ToString(),
                "package" => f => f.PackageId?.ToString() ?? "",
                "drg" => f => f.DrgId?.ToString() ?? "",
                "encounterType" => f => f.EncType,
                _ => f => f.Segment,
            };
            string Label(string k) => dimension is "encounterType" or "segment" ? k
                : Guid.TryParse(k, out var g) ? labels.GetValueOrDefault(g, k) : "Not assigned";
            string? Link(string k) => dimension switch
            {
                "package" when k != "" => $"packages/{k}", "drg" when k != "" => $"drgs/{k}", _ => null,
            };
            rows = facts.GroupBy(key).Select(g => Make(g.Key, Label(g.Key), g.Count(), g.Count(), g.Sum(x => x.Revenue),
                g.Sum(x => x.Direct), g.Sum(x => x.Indirect), g.Sum(x => x.Overhead), Link(g.Key))).ToList();
        }

        rows = rows.OrderByDescending(r => showRevenue ? r.Operating : r.Total).ToList();
        var total = Make("total", "Total", rows.Sum(r => r.Encounters), rows.Sum(r => r.Volume), rows.Sum(r => r.Revenue), rows.Sum(r => r.Direct),
            rows.Sum(r => r.Indirect), rows.Sum(r => r.Overhead), null);
        return new ProfitDto(dimension, rows.ToArray(), total, idle, showRevenue ? Money.R4(total.Operating - idle) : 0m);
    }

    // ═══ Variance (FR-6) ═════════════════════════════════════════════════════════════

    public sealed record VarianceRow(Guid Id, string Scope, Guid ScopeId, string Label, string Baseline, decimal Actual, decimal BaselineAmount,
        decimal Variance, decimal VariancePct, string Direction, int Cases, decimal RateEffect, decimal QuantityEffect, decimal MixEffect, string Summary);
    public sealed record VarianceComponentDto(string ComponentType, string Label, decimal StandardQty, decimal ActualQty, decimal StandardRate,
        decimal ActualRate, decimal RateEffect, decimal QuantityEffect, decimal MixEffect, decimal TotalEffect, string Explanation);
    public sealed record VarianceDetail(VarianceRow Variance, VarianceComponentDto[] Components, bool IdentityHolds);

    public async Task<VarianceRow[]> VariancesAsync(ScopedRun s, CancellationToken ct)
    {
        var vs = await db.Variances.AsNoTracking().Where(v => v.RunId == s.Run.Id).ToListAsync(ct);
        var ids = vs.Select(v => v.Id).ToList();
        var effects = await db.VarianceComponents.AsNoTracking().Where(c => ids.Contains(c.VarianceId))
            .GroupBy(c => c.VarianceId).Select(g => new { g.Key, R = g.Sum(c => c.RateEffect), Q = g.Sum(c => c.QuantityEffect), M = g.Sum(c => c.MixEffect) })
            .ToDictionaryAsync(x => x.Key, ct);
        return vs.OrderByDescending(v => Math.Abs(v.VarianceAmount)).Select(v =>
        {
            effects.TryGetValue(v.Id, out var e);
            return new VarianceRow(v.Id, v.Scope, v.ScopeId, v.ScopeLabel, v.Baseline, v.ActualAmount, v.BaselineAmount, v.VarianceAmount, v.VariancePct,
                v.Direction, v.CaseCount, e?.R ?? 0, e?.Q ?? 0, e?.M ?? 0, v.Summary);
        }).ToArray();
    }

    public async Task<VarianceDetail> VarianceAsync(ScopedRun s, Guid id, CancellationToken ct)
    {
        var row = (await VariancesAsync(s, ct)).FirstOrDefault(v => v.Id == id) ?? throw AppError.NotFound("Variance");
        var comps = await db.VarianceComponents.AsNoTracking().Where(c => c.VarianceId == id).OrderByDescending(c => c.TotalEffect).ToListAsync(ct);
        // AC-2: rate + quantity + mix = actual - standard, per line, to the minor unit.
        var holds = comps.All(c => c.RateEffect + c.QuantityEffect + c.MixEffect == c.TotalEffect);
        return new VarianceDetail(row, comps.Select(c => new VarianceComponentDto(c.ComponentType, c.Label, c.StandardQty, c.ActualQty, c.StandardRate,
            c.ActualRate, c.RateEffect, c.QuantityEffect, c.MixEffect, c.TotalEffect, c.Explanation)).ToArray(), holds);
    }

    // ═══ Capacity (FR-4) ═════════════════════════════════════════════════════════════

    public sealed record CapacityRow(Guid ResourceId, string Code, string Name, string ResourceType, string Uom, bool IsCapacityDriven, string CostCentre,
        string? AnnualCost, decimal PeriodCost, decimal TheoreticalMinutes, decimal PracticalMinutes, decimal AvailableMinutes, decimal UsedMinutes,
        decimal UnusedMinutes, string? UnitCost, decimal CostOfUsed, decimal CostOfUnused, decimal UtilisationPct, string CalcExpression);
    public sealed record CapacityDto(CapacityRow[] Rows, decimal Idle, decimal OverCapacity, decimal SupplyWaste, decimal CapacityPaid, decimal CapacityUsed);

    public async Task<CapacityDto> CapacityAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var rows = await (from u in db.ResourceUnitCosts.AsNoTracking()
                          join r in db.Resources on u.ResourceId equals r.Id
                          join c in db.CostCenters on r.CostCenterId equals c.Id
                          where u.RunId == s.Run.Id
                          orderby r.ResourceType, r.Code
                          select new { u, r, Cc = c.Name }).ToListAsync(ct);
        var residuals = await db.ResidualBuckets.AsNoTracking().Where(b => b.RunId == s.Run.Id).ToListAsync(ct);
        var mapped = rows.Select(x => new CapacityRow(x.r.Id, x.r.Code, x.r.Name, x.r.ResourceType, x.u.Uom, x.r.IsCapacityDriven, x.Cc,
            x.r.ResourceType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : x.u.AnnualCost.ToString("0.0000"),
            x.u.PeriodCost, x.u.TheoreticalMinutes, x.u.PracticalMinutes, x.u.AvailableMinutes, x.u.UsedMinutes, x.u.UnusedMinutes,
            Masked(p, x.r.ResourceType, x.u.UnitCost), x.u.CostOfUsedCapacity, x.u.CostOfUnusedCapacity, x.u.UtilisationPct,
            x.r.ResourceType.StartsWith("LABOR_") && !p.Can("salary.view") ? "Labour rates are masked for your role (salary.view)." : x.u.CalcExpression)).ToArray();
        var cap = rows.Where(x => x.r.IsCapacityDriven).ToList();
        return new CapacityDto(mapped,
            residuals.Where(b => b.Bucket == "UNUSED_CAPACITY").Sum(b => b.Amount), residuals.Where(b => b.Bucket == "OVER_CAPACITY").Sum(b => b.Amount),
            rows.Where(x => !x.r.IsCapacityDriven).Sum(x => x.u.CostOfUnusedCapacity), cap.Sum(x => x.u.PeriodCost), cap.Sum(x => x.u.CostOfUsedCapacity));
    }

    // ═══ Reconciliation (FR-7) ═══════════════════════════════════════════════════════

    public sealed record ReconLine(int Sequence, string Label, string LineType, string? Bucket, decimal Amount, decimal RunningBalance, string Explanation);
    public sealed record ResidualDto(string Bucket, string Label, decimal Amount, string Owner, string Explanation, System.Text.Json.JsonElement? Detail);
    public sealed record LedgerRow(string Key, string Label, decimal Amount, decimal Share);
    public sealed record ReconDto(decimal GlTotal, decimal PatientCost, decimal ResidualTotal, decimal Unexplained, decimal Materiality,
        decimal AttributionPct, string Status, string Narrative, ReconLine[] Lines, ResidualDto[] Residuals, LedgerRow[] ByAccount, LedgerRow[] ByCostCentre,
        LedgerRow[] ByNaturalClass);

    public async Task<ReconDto> ReconciliationAsync(ScopedRun s, CancellationToken ct)
    {
        var rec = await db.Reconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == s.Run.Id, ct)
                  ?? throw AppError.NotCosted($"Run {s.Run.Code} has not reconciled.");
        var lines = await db.ReconciliationLines.AsNoTracking().Where(l => l.ReconciliationId == rec.Id).OrderBy(l => l.Sequence).ToListAsync(ct);
        var residuals = await db.ResidualBuckets.AsNoTracking().Where(b => b.RunId == s.Run.Id).OrderByDescending(b => b.Amount).ToListAsync(ct);
        // FR-7.14: the ledger by natural account and by cost centre, to tie to the trial balance.
        var ledger = await (from g in db.GLPostings.AsNoTracking()
                            join a in db.GLAccounts on g.GLAccountId equals a.Id
                            join c in db.CostCenters on g.CostCenterId equals c.Id
                            where g.HospitalId == s.Hospital.Id && g.PeriodId == s.Period.Id
                            select new { a.Code, a.Name, a.NaturalClass, Cc = c.Code, CcName = c.Name, g.Amount }).ToListAsync(ct);
        var gl = ledger.Sum(x => x.Amount);
        LedgerRow[] By(IEnumerable<(string Key, string Label, decimal Amount)> rows) =>
            rows.OrderByDescending(r => r.Amount).Select(r => new LedgerRow(r.Key, r.Label, Money.R4(r.Amount), Share(r.Amount, gl))).ToArray();
        return new ReconDto(rec.GlTotal, rec.PatientCost, rec.ResidualTotal, rec.Unexplained, rec.Materiality, rec.AttributionPct, rec.Status, rec.Narrative,
            lines.Select(l => new ReconLine(l.Sequence, l.Label, l.LineType, l.Bucket, l.Amount, l.RunningBalance, l.Explanation)).ToArray(),
            residuals.Select(b => new ResidualDto(b.Bucket, b.Label, b.Amount, Catalogue.ResidualOwner[b.Bucket], b.Explanation,
                b.DetailJson is null ? null : System.Text.Json.JsonDocument.Parse(b.DetailJson).RootElement)).ToArray(),
            By(ledger.GroupBy(x => x.Code).Select(g => (g.Key, $"{g.Key} {g.First().Name}", g.Sum(x => x.Amount)))),
            By(ledger.GroupBy(x => x.Cc).Select(g => (g.Key, g.First().CcName, g.Sum(x => x.Amount)))),
            By(ledger.GroupBy(x => x.NaturalClass).Select(g => (g.Key, g.Key, g.Sum(x => x.Amount)))));
    }

    // ═══ Data quality (FR-9) ═════════════════════════════════════════════════════════

    public sealed record CheckDto(string Code, string Name, string Category, string Severity, string Question, int Issues, string Result);
    public sealed record IssueDto(Guid Id, string CheckCode, string Severity, string EntityType, string? EntityId, string EntityLabel, string Message, string? Value, string Status);
    public sealed record QualityDto(CheckDto[] Register, IssueDto[] Issues, int Passed, int Failed);

    public async Task<QualityDto> QualityAsync(ScopedRun s, CancellationToken ct)
    {
        var issues = await db.DataQualityIssues.AsNoTracking().Where(i => i.RunId == s.Run.Id).ToListAsync(ct);
        var executed = s.Run.Status is not ("DRAFT" or "VALIDATING");
        // FR-9.4: passes are reported as well as failures.
        var register = QualityChecks.All.Select(c =>
        {
            var n = issues.Count(i => i.CheckCode == c.Code);
            return new CheckDto(c.Code, c.Name, c.Category, c.Severity, c.Question, n, !executed ? "NOT_RUN" : n == 0 ? "PASS" : "FAIL");
        }).ToArray();
        return new QualityDto(register,
            issues.OrderBy(i => Array.IndexOf(Catalogue.Severities, i.Severity)).ThenBy(i => i.CheckCode)
                .Select(i => new IssueDto(i.Id, i.CheckCode, i.Severity, i.EntityType, i.EntityId, i.EntityLabel, i.Message, i.Value, i.Status)).ToArray(),
            register.Count(r => r.Result == "PASS"), register.Count(r => r.Result == "FAIL"));
    }
}
