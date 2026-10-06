using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

/// <summary>
/// Stage 1 — the data-quality gate (FR-9). Runs before any money moves: the alternative is
/// discovering a missing rate partway through a run and having to explain why half the month's
/// costs are understated.
/// </summary>
public static class Quality
{
    public sealed record GateResult(StepResult Step, int Blocking, int Errors);

    public static GateResult Validate(RunContext ctx, UsageIndex usage, StageOutput output)
    {
        var issues = new List<DataQualityIssue>();
        void Add(string code, string entityType, string? entityId, string label, string message, string? value = null)
        {
            var def = QualityChecks.Get(code);
            issues.Add(new DataQualityIssue
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, CheckCode = code, CheckName = def.Name,
                Category = def.Category, Severity = def.Severity, EntityType = entityType, EntityId = entityId,
                EntityLabel = label, Message = message, Value = value, DetectedAt = ctx.CalculatedAt, Status = "OPEN",
            });
        }

        var facts = usage.Facts;
        var asOf = Fmt.Date(ctx.AsOf);

        // ── Master data ───────────────────────────────────────────────────────────
        foreach (var (resourceId, c) in facts.ConsumedByResource)
        {
            if (!ctx.Resources.TryGetValue(resourceId, out var res)) continue;
            if (res.RateId is null || res.Rate <= 0m)
                Add("DQ-RATE-MISSING", "Resource", res.Id.ToString(), res.Name,
                    $"{res.Name} was consumed {c.Count} times but has no rate effective at {asOf}.", Fmt.R(c.Quantity));
        }

        // CR-9: two open-ended or overlapping spans mean no single effective rate.
        foreach (var group in ctx.RateSpans.GroupBy(r => r.ResourceId))
        {
            var spans = group.OrderBy(s => s.From).ToArray();
            var pairs = 0;
            for (var i = 0; i < spans.Length; i++)
                for (var j = i + 1; j < spans.Length; j++)
                    if (spans[i].From <= (spans[j].To ?? DateOnly.MaxValue) && (spans[i].To ?? DateOnly.MaxValue) >= spans[j].From)
                        pairs++;
            if (pairs > 0 && ctx.Resources.TryGetValue(group.Key, out var res))
                Add("DQ-RATE-OVERLAP", "Resource", res.Id.ToString(), res.Name,
                    $"{res.Name} has {pairs} pair(s) of rates covering the same dates; the effective rate at any moment must be unique.");
        }

        // ── Financial structure ───────────────────────────────────────────────────
        foreach (var pair in ctx.Mappings.GroupBy(m => (m.GlAccountId, m.CostCenterId)))
        {
            var weight = pair.Sum(m => m.Weight);
            if (Math.Abs(weight - 1m) > 0.000001m)
            {
                var label = $"{pair.First().AccountLabel} @ {pair.First().CostCenterName}";
                Add("DQ-GL-WEIGHT", "GLMapping", null, label,
                    $"Cost-pool weights for {label} total {Fmt.P(weight * 100m, 2)}%, not 100%.", weight.ToString("0.000000"));
            }
        }

        var mapped = ctx.Mappings.Select(m => (m.GlAccountId, m.CostCenterId)).ToHashSet();
        foreach (var g in ctx.Postings.Where(p => !mapped.Contains((p.GlAccountId, p.CostCenterId))).GroupBy(p => (p.GlAccountId, p.CostCenterId)))
        {
            var first = g.First();
            var cc = ctx.CcById.TryGetValue(first.CostCenterId, out var ci) ? ci.Name : first.CostCenterId.ToString();
            var amount = g.Sum(p => p.Amount);
            Add("DQ-GL-UNMAPPED", "GLAccount", first.GlAccountId.ToString(), $"{first.AccountCode} {first.AccountName} @ {cc}",
                $"{Fmt.Sar(amount)} posted to {first.AccountCode} {first.AccountName} at {cc} has no effective cost-pool mapping, so it will sit in the UNMAPPED_GL residual.",
                Money.R4(amount).ToString("0.0000"));
        }

        var poolCcs = ctx.Pools.Select(p => p.CostCenterId).ToHashSet();
        foreach (var g in ctx.Postings.GroupBy(p => p.CostCenterId).Where(g => !poolCcs.Contains(g.Key)))
        {
            var name = ctx.CcById.TryGetValue(g.Key, out var cc) ? cc.Name : g.Key.ToString();
            var amount = g.Sum(p => p.Amount);
            Add("DQ-CC-NO-POOL", "CostCenter", g.Key.ToString(), name,
                $"{name} carries {Fmt.Sar(amount)} of spend but has no cost pool defined.", Money.R4(amount).ToString("0.0000"));
        }

        // ── Allocation rules ──────────────────────────────────────────────────────
        var shareByPool = new Dictionary<Guid, decimal>();
        foreach (var r in ctx.Rules)
        {
            shareByPool[r.SourcePoolId] = shareByPool.GetValueOrDefault(r.SourcePoolId) + r.PoolShare;
            if (r.Method == "PERCENTAGE")
            {
                var total = r.Targets.Sum(t => t.Percentage ?? t.Weight);
                if (Math.Abs(total - 1m) > 0.000001m)
                    Add("DQ-ALLOC-WEIGHT", "AllocationRule", r.Id.ToString(), r.Name,
                        $"{r.Name} allocates {Fmt.P(total * 100m, 2)}% across its targets, not 100%.", total.ToString("0.000000"));
            }
            else if (r.DriverId is null)
            {
                Add("DQ-DRIVER-MISSING", "AllocationRule", r.Id.ToString(), r.Name, $"{r.Name} uses the {r.Method} method but names no cost driver.");
            }
            else
            {
                var volume = ctx.CostCenters.Sum(cc => ctx.DriverVolume(r.DriverId.Value, cc.Id));
                if (volume <= 0m)
                {
                    var name = ctx.Drivers.TryGetValue(r.DriverId.Value, out var d) ? d.Name : "Driver";
                    Add("DQ-DRIVER-ZERO", "CostDriver", r.DriverId.ToString(), name,
                        $"{name} recorded no volume in {ctx.PeriodCode}, so {r.Name} has nothing to allocate on.");
                }
            }
        }
        foreach (var (poolId, share) in shareByPool)
        {
            if (Math.Abs(share - 1m) <= 0.000001m) continue;
            var name = ctx.PoolById.TryGetValue(poolId, out var p) ? p.Name : poolId.ToString();
            Add("DQ-ALLOC-POOLSHARE", "CostPool", poolId.ToString(), name,
                $"Rules on {name} together move {Fmt.P(share * 100m, 2)}% of it, not 100%.", share.ToString("0.000000"));
        }

        // ── Source data ───────────────────────────────────────────────────────────
        if (facts.NegativeQuantities > 0)
            Add("DQ-COST-NEGATIVE", "ActivityTransaction", null, $"{ctx.PeriodCode} transactions",
                $"{facts.NegativeQuantities} transaction(s) recorded a negative quantity, which would produce a negative cost.",
                facts.NegativeQuantities.ToString());

        foreach (var (sourceRef, n) in facts.SourceRefCounts.Where(kv => kv.Value > 1).Take(50))
            Add("DQ-TXN-DUPLICATE", "ActivityTransaction", null, sourceRef,
                $"Source reference {sourceRef} appears {n} times; the same source event has been loaded more than once.", n.ToString());

        foreach (var (activityId, d) in facts.DurationOutliers.Take(50))
        {
            if (!ctx.Activities.TryGetValue(activityId, out var act)) continue;
            Add("DQ-DURATION-IMPOSSIBLE", "Activity", act.Id.ToString(), act.Name,
                $"{d.Count} record(s) for {act.Name} exceed three times its declared maximum of {Fmt.N(act.MaxDuration, 0)} minutes per occurrence (largest {Fmt.N(d.MaxPerOccurrence, 0)}).",
                Money.R2(d.MaxPerOccurrence).ToString());
        }

        // ── Definitions ───────────────────────────────────────────────────────────
        foreach (var g in ctx.Services.GroupBy(s => s.ProcedureId))
        {
            if (!ctx.Procedures.TryGetValue(g.Key, out var proc)) continue;
            if (proc.ActivityLines.Count == 0 && proc.ResourceLines.Count == 0)
                Add("DQ-PROC-UNMAPPED", "Procedure", proc.Id.ToString(), proc.Name,
                    $"{proc.Name} was delivered {g.Count()} times but has no costing definition under {ctx.CostingVersionCode}.", g.Count().ToString());
        }

        foreach (var act in ctx.Activities.Values.Where(a => a.Directness == "DIRECT" && a.Recipe.Count == 0))
            Add("DQ-ACT-NO-RECIPE", "Activity", act.Id.ToString(), act.Name,
                $"{act.Name} is a direct activity with no standard resource recipe, so it has no standard cost.");

        var withServices = ctx.Services.Select(s => s.EncounterId).ToHashSet();
        var empty = ctx.Encounters.Count(e => !withServices.Contains(e.Id));
        if (empty > 0)
            Add("DQ-ENC-NO-SERVICE", "Encounter", null, $"{ctx.PeriodCode} encounters", $"{empty} encounter(s) recorded no services at all.", empty.ToString());

        var withRevenue = ctx.Revenue.Select(r => r.EncounterId).ToHashSet();
        var noRevenue = ctx.Encounters.Count(e => withServices.Contains(e.Id) && !withRevenue.Contains(e.Id));
        if (noRevenue > 0)
            Add("DQ-ENC-NO-REVENUE", "Encounter", null, $"{ctx.PeriodCode} encounters",
                $"{noRevenue} encounter(s) have clinical activity but nothing billed against them.", noRevenue.ToString());

        output.Issues.AddRange(issues);

        var blocking = issues.Count(i => i.Severity == "BLOCKING");
        var errors = issues.Count(i => i.Severity == "ERROR");
        var warnings = issues.Count(i => i.Severity == "WARNING");
        var checksRun = QualityChecks.All.Count(c => !c.AfterCapacity);
        var failedCodes = issues.Select(i => i.CheckCode).ToHashSet();

        return new GateResult(new StepResult(
            issues.Count, 0m, 0m,
            blocking > 0 ? "FAILED" : issues.Count > 0 ? "WARNING" : "SUCCESS",
            issues.Count == 0
                ? $"All {checksRun} pre-calculation checks passed."
                : $"{checksRun} checks run: {checksRun - failedCodes.Count} passed; {blocking} blocking, {errors} error, {warnings} warning issue(s).",
            new Dictionary<string, object?>
            {
                ["checksRun"] = checksRun, ["issues"] = issues.Count, ["blocking"] = blocking, ["errors"] = errors, ["warnings"] = warnings,
                ["passed"] = QualityChecks.All.Where(c => !c.AfterCapacity && !failedCodes.Contains(c.Code)).Select(c => c.Code).ToArray(),
            }), blocking, errors);
    }

    /// <summary>
    /// Capacity checks run after stage 4, since they need its utilisation (FR-4.12, FR-9.9).
    /// DQ-UTIL-ZERO exempts resources in support and overhead centres, which are fully allocated
    /// out and will always show zero (FR-9.7).
    /// </summary>
    public static int ValidateCapacity(RunContext ctx, IReadOnlyList<ResourceUnitCost> unitCosts, StageOutput output)
    {
        var n = 0;
        foreach (var u in unitCosts)
        {
            if (!ctx.Resources.TryGetValue(u.ResourceId, out var res) || !res.IsCapacityDriven || u.AvailableMinutes <= 0m) continue;
            var ccType = ctx.CcById.TryGetValue(res.CostCenterId, out var cc) ? cc.CcType : "";
            if (u.UtilisationPct <= 0.01m)
            {
                if (ccType is "SUPPORT" or "OVERHEAD") continue;
                output.Issues.Add(Issue(ctx, "DQ-UTIL-ZERO", res,
                    $"{res.Name} recorded no use at all this period, while {Fmt.Sar(u.CostOfUnusedCapacity)} of its capacity was paid for.", "0"));
                n++;
            }
            else if (u.UtilisationPct > 100m)
            {
                output.Issues.Add(Issue(ctx, "DQ-UTIL-OVER", res,
                    $"{res.Name} ran at {Fmt.P(u.UtilisationPct)}% of practical capacity — beyond the capacity it is modelled at, which usually means overtime or extended sessions.",
                    Fmt.P(u.UtilisationPct)));
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Abnormal consumption: encounters far outside their DRG's interquartile range (beyond
    /// P75 + 3 x IQR, Tukey's "far out" fence). Reported per DRG so the list stays actionable.
    /// </summary>
    public static int ValidateConsumption(RunContext ctx, IReadOnlyDictionary<Guid, decimal> encounterTotals, StageOutput output)
    {
        var n = 0;
        foreach (var g in ctx.Encounters.Where(e => e.DrgId is not null && encounterTotals.ContainsKey(e.Id)).GroupBy(e => e.DrgId!.Value))
        {
            var costs = g.Select(e => encounterTotals[e.Id]).ToArray();
            if (costs.Length < 8) continue;
            var p25 = Money.Percentile(costs, 25m);
            var p75 = Money.Percentile(costs, 75m);
            var fence = p75 + 3m * (p75 - p25);
            var far = costs.Count(c => c > fence);
            if (far == 0) continue;
            var def = QualityChecks.Get("DQ-CONSUMPTION-ABNORMAL");
            output.Issues.Add(new DataQualityIssue
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, CheckCode = def.Code, CheckName = def.Name, Category = def.Category,
                Severity = def.Severity, EntityType = "DRG", EntityId = g.Key.ToString(), EntityLabel = $"DRG {g.Key}",
                Message = $"{far} encounter(s) cost more than {Fmt.Sar(fence)}, three interquartile ranges above the DRG's upper quartile — check the consumption recorded against them.",
                Value = far.ToString(), DetectedAt = ctx.CalculatedAt, Status = "OPEN",
            });
            n++;
        }
        return n;
    }

    private static DataQualityIssue Issue(RunContext ctx, string code, ResInfo res, string message, string value)
    {
        var def = QualityChecks.Get(code);
        return new DataQualityIssue
        {
            Id = IdGen.Sequential(), RunId = ctx.RunId, CheckCode = code, CheckName = def.Name, Category = def.Category,
            Severity = def.Severity, EntityType = "Resource", EntityId = res.Id.ToString(), EntityLabel = res.Name,
            Message = message, Value = value, DetectedAt = ctx.CalculatedAt, Status = "OPEN",
        };
    }
}
