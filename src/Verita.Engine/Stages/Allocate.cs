using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

public sealed class SupportAllocationResult
{
    /// <summary>costCenterId -> balance available for activity allocation.</summary>
    public Dictionary<Guid, decimal> CcFinal { get; } = [];
    public int ReciprocalIterations { get; set; }
    public decimal ReciprocalResidual { get; set; }
}

public sealed class ActivityAllocationResult
{
    public Dictionary<Guid, decimal> ActivityOverhead { get; } = [];
    /// <summary>serviceId -> overhead carried by that service line (FR-3.14).</summary>
    public Dictionary<Guid, decimal> ServiceOverhead { get; } = [];
    public decimal OrphanedOverhead { get; set; }
    public List<object> OrphanDetail { get; } = [];
    public decimal NoDriverVolume { get; set; }
    public List<object> NoDriverDetail { get; } = [];
}

/// <summary>Stages 5 and 6 — allocation (FR-3).</summary>
public static class Allocate
{
    public const decimal Tolerance = 0.01m;
    public const int MaxIterations = 100;

    private sealed record Share(CcInfo Cc, decimal Fraction, decimal Qty);

    /// <summary>
    /// Each target's share of a rule. PERCENTAGE uses the weights a human agreed; every other
    /// method uses a measured driver volume. A rule never falls back from one to the other — a
    /// zero volume returns null and the caller routes the money to a residual (CR-13).
    /// </summary>
    private static (List<Share> Shares, decimal Volume)? SharesFor(RuleInfo rule, IReadOnlyList<CcInfo> candidates, RunContext ctx)
    {
        if (rule.Method == "PERCENTAGE" && rule.Targets.Count > 0)
        {
            var targets = rule.Targets
                .Select(t => (Cc: candidates.FirstOrDefault(c => c.Id == t.CostCenterId), Pct: t.Percentage ?? t.Weight))
                .Where(t => t.Cc is not null).ToList();
            var total = targets.Sum(t => t.Pct);
            if (total <= 0m) return null;
            return (targets.Select(t => new Share(t.Cc!, t.Pct / total, t.Pct)).ToList(), total);
        }

        if (rule.DriverId is not { } driverId) return null;
        var quantities = candidates.Select(cc => (Cc: cc, Qty: ctx.DriverVolume(driverId, cc.Id))).ToList();
        var volume = quantities.Sum(q => q.Qty);
        if (volume <= 0m) return null;
        return (quantities.Where(q => q.Qty > 0m).Select(q => new Share(q.Cc, q.Qty / volume, q.Qty)).ToList(), volume);
    }

    /// <summary>
    /// Stage 5 — support and overhead. Pass 1 resolves mutual service among reciprocal centres by
    /// repeated redistribution (FR-3.4); pass 2 steps everything else down to operating centres
    /// in ascending StepDownSeq, closing each centre as it goes (FR-3.3).
    /// </summary>
    public static (SupportAllocationResult Result, StepResult Step) Support(RunContext ctx, PoolBuildResult pools, StageOutput output)
    {
        var result = new SupportAllocationResult();
        var allocationPools = ctx.Pools.Where(p => p.IsAllocationStream).Select(p => p.Id).ToHashSet();

        var ccDirect = new Dictionary<Guid, decimal>();
        foreach (var (poolId, amount) in pools.PoolDirect)
        {
            if (!allocationPools.Contains(poolId) || !ctx.PoolById.TryGetValue(poolId, out var pool)) continue;
            ccDirect[pool.CostCenterId] = ccDirect.GetValueOrDefault(pool.CostCenterId) + amount;
        }

        var received = new Dictionary<Guid, decimal>();
        var allocatedOut = new Dictionary<Guid, decimal>();
        static void Bump(Dictionary<Guid, decimal> m, Guid k, decimal v) => m[k] = m.GetValueOrDefault(k) + v;

        var stage1 = ctx.Rules.Where(r => r.Stage == 1).ToList();
        var stage2 = ctx.Rules.Where(r => r.Stage == 2).ToList();
        var reciprocalCcs = stage1.Select(r => r.SourceCostCenterId).ToHashSet();

        // ── Pass 1: reciprocal ────────────────────────────────────────────────────
        var residual = reciprocalCcs.ToDictionary(id => id, id => ccDirect.GetValueOrDefault(id));
        var totals = new Dictionary<(Guid Rule, Guid Cc), (RuleInfo Rule, CcInfo Cc, decimal Qty, decimal Volume, decimal Amount)>();
        var iterations = 0;

        while (iterations < MaxIterations)
        {
            if (residual.Values.Sum() <= Tolerance) break;
            iterations++;
            var next = new Dictionary<Guid, decimal>();

            foreach (var (sourceCcId, balance) in residual)
            {
                if (balance <= Tolerance / 10m) continue;
                var rules = stage1.Where(r => r.SourceCostCenterId == sourceCcId).ToList();
                if (rules.Count == 0) continue;
                // FR-3.5: a source never allocates to itself.
                var candidates = ctx.CostCenters.Where(c => c.Id != sourceCcId).ToList();

                foreach (var rule in rules)
                {
                    var amount = balance * rule.PoolShare;
                    if (amount <= 0m) continue;
                    var computed = SharesFor(rule, candidates, ctx);
                    if (computed is null) continue;
                    var (shares, volume) = computed.Value;

                    var parts = Money.Distribute(amount, shares.Select(s => s.Fraction).ToArray(), ctx.DistributionScale);
                    for (var i = 0; i < shares.Count; i++)
                    {
                        var portion = parts[i];
                        if (portion == 0m) continue;
                        var key = (rule.Id, shares[i].Cc.Id);
                        totals[key] = totals.TryGetValue(key, out var t)
                            ? t with { Amount = t.Amount + portion }
                            : (rule, shares[i].Cc, shares[i].Qty, volume, portion);

                        // CR-5: inflow is recorded on the balance sheet AND carried as the next
                        // iteration's residual. Tracking it only in the residual makes a centre
                        // close negative by exactly the amount that circulated back.
                        Bump(received, shares[i].Cc.Id, portion);
                        if (reciprocalCcs.Contains(shares[i].Cc.Id)) Bump(next, shares[i].Cc.Id, portion);
                    }
                    Bump(allocatedOut, sourceCcId, parts.Sum());
                }
            }
            residual = next;
        }
        var finalResidual = residual.Values.Sum();

        // FR-3.6: one row per (rule, target) carrying the total moved across all iterations.
        foreach (var (rule, cc, qty, volume, amount) in totals.Values)
        {
            var driver = rule.DriverId is { } d && ctx.Drivers.TryGetValue(d, out var di) ? di : null;
            var share = volume > 0m ? qty / volume : 0m;
            output.Allocations.Add(new AllocationResult
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, RuleId = rule.Id, SourcePoolId = rule.SourcePoolId,
                SourceCostCenterId = rule.SourceCostCenterId, TargetType = "COST_CENTER", TargetCostCenterId = cc.Id,
                TargetLabel = cc.Name, Stage = 1, Iteration = iterations, DriverId = rule.DriverId,
                DriverQty = Money.R6(qty), DriverVolume = Money.R6(volume), DriverShare = Money.R6(share), Amount = Money.R4(amount),
                CalcExpression =
                    $"{rule.Name}: reciprocal allocation converged in {iterations} {Fmt.Plural(iterations, "iteration")}. " +
                    $"{cc.Name} share {Fmt.N(qty)} / {Fmt.N(volume)} {driver?.Unit ?? "units"} = {Fmt.P(share * 100m, 2)}% -> SAR {Fmt.N(amount)}",
            });
        }

        // ── Pass 2: step-down ─────────────────────────────────────────────────────
        var closed = new HashSet<Guid>(reciprocalCcs);
        var stepSources = stage2.Select(r => r.SourceCostCenterId).Distinct()
            .Select(id => ctx.CcById.TryGetValue(id, out var c) ? c : null).OfType<CcInfo>()
            .OrderBy(c => c.StepDownSeq).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();

        foreach (var source in stepSources)
        {
            var balance = ccDirect.GetValueOrDefault(source.Id) + received.GetValueOrDefault(source.Id) - allocatedOut.GetValueOrDefault(source.Id);
            closed.Add(source.Id);
            if (balance <= Tolerance) continue;

            var rules = stage2.Where(r => r.SourceCostCenterId == source.Id).OrderBy(r => r.Sequence).ToList();
            var candidates = ctx.CostCenters.Where(c => !closed.Contains(c.Id)).ToList();

            foreach (var rule in rules)
            {
                var amount = balance * rule.PoolShare;
                if (amount <= 0m) continue;
                var computed = SharesFor(rule, candidates, ctx);
                if (computed is null) continue;
                var (shares, volume) = computed.Value;
                var parts = Money.Distribute(amount, shares.Select(s => s.Fraction).ToArray(), ctx.DistributionScale);
                var driver = rule.DriverId is { } d && ctx.Drivers.TryGetValue(d, out var di) ? di : null;

                for (var i = 0; i < shares.Count; i++)
                {
                    var portion = parts[i];
                    if (portion == 0m) continue;
                    var s = shares[i];
                    Bump(received, s.Cc.Id, portion);
                    output.Allocations.Add(new AllocationResult
                    {
                        Id = IdGen.Sequential(), RunId = ctx.RunId, RuleId = rule.Id, SourcePoolId = rule.SourcePoolId,
                        SourceCostCenterId = source.Id, TargetType = "COST_CENTER", TargetCostCenterId = s.Cc.Id, TargetLabel = s.Cc.Name,
                        Stage = 2, Iteration = 0, DriverId = rule.DriverId,
                        DriverQty = Money.R6(s.Qty), DriverVolume = Money.R6(volume), DriverShare = Money.R6(s.Fraction), Amount = Money.R4(portion),
                        CalcExpression = rule.Method == "PERCENTAGE"
                            ? $"{rule.Name}: agreed {Fmt.P(s.Fraction * 100m)}% of SAR {Fmt.N(amount)} to {s.Cc.Name} = SAR {Fmt.N(portion)}"
                            : $"{rule.Name}: SAR {Fmt.N(amount)} x ({Fmt.N(s.Qty)} / {Fmt.N(volume)} {driver?.Unit ?? "units"}) = SAR {Fmt.N(portion)}",
                    });
                }
                Bump(allocatedOut, source.Id, parts.Sum());
            }
        }

        // ── Balances ──────────────────────────────────────────────────────────────
        foreach (var cc in ctx.CostCenters)
        {
            var direct = ccDirect.GetValueOrDefault(cc.Id);
            var rec = received.GetValueOrDefault(cc.Id);
            var outAmt = allocatedOut.GetValueOrDefault(cc.Id);
            var final = direct + rec - outAmt;
            result.CcFinal[cc.Id] = final;
            output.CcBalances.Add(new CostCenterBalance
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, CostCenterId = cc.Id,
                DirectAmount = Money.R4(direct), ReceivedAmount = Money.R4(rec), AllocatedOut = Money.R4(outAmt),
                FinalAmount = Money.R4(final), StepDownSeq = cc.StepDownSeq, IsClosed = closed.Contains(cc.Id),
            });
        }

        result.ReciprocalIterations = iterations;
        result.ReciprocalResidual = Money.R4(finalResidual);

        var directTotal = ccDirect.Values.Sum();
        var finalTotal = result.CcFinal.Values.Sum();
        // AC-3: a stage moves money, it never creates or destroys it.
        var conserved = Money.Eq(directTotal, finalTotal, 0.0001m * Math.Max(1, output.Allocations.Count));
        return (result, new StepResult(
            output.Allocations.Count + output.CcBalances.Count, Money.R4(directTotal), Money.R4(finalTotal),
            !conserved ? "FAILED" : finalResidual > 1m ? "WARNING" : "SUCCESS",
            !conserved
                ? $"Support allocation did not conserve: {Fmt.Sar(directTotal - finalTotal, 4)} created or lost."
                : $"Reciprocal pass converged in {iterations} {Fmt.Plural(iterations, "iteration")} (residual {Fmt.Sar(finalResidual, 2)}); step-down closed {stepSources.Count} cost centres.",
            new Dictionary<string, object?>
            {
                ["reciprocalIterations"] = iterations, ["reciprocalResidual"] = Money.R4(finalResidual),
                ["stepDownSources"] = stepSources.Count, ["allocationRows"] = output.Allocations.Count,
            }));
    }

    /// <summary>FR-3.11: driver quantity per activity, by the driver's type.</summary>
    private static decimal ActivityDriverQty(DriverInfo? driver, UsageIndex usage, Guid activityId)
    {
        if (!usage.ByActivity.TryGetValue(activityId, out var au)) return 0m;
        if (driver is null) return au.ResourceCost;
        return driver.DriverType switch
        {
            "TIME" => au.Minutes,
            "VALUE" => au.DrugValue > 0m ? au.DrugValue : au.ResourceCost,
            "VOLUME" or "QUANTITY" => au.Occurrences,
            // FR-3.12: structural drivers have no activity-level measurement, so the activity's
            // own direct resource cost stands in — declared as value-based in the expression.
            _ => au.ResourceCost,
        };
    }

    private static bool IsMeasured(DriverInfo? d) => d is not null && d.DriverType is "TIME" or "VALUE" or "VOLUME" or "QUANTITY";

    /// <summary>
    /// Stage 6 — operating cost centre balances to activities (FR-3.10), then activity overhead
    /// pushed down to the services that consumed each activity, pro rata to their direct
    /// resource cost within it (FR-3.14).
    /// </summary>
    public static (ActivityAllocationResult Result, StepResult Step) ToActivities(RunContext ctx, UsageIndex usage, SupportAllocationResult support, StageOutput output)
    {
        var result = new ActivityAllocationResult();
        var multiplier = ctx.Overrides.OverheadRate;
        var startRows = output.Allocations.Count;

        var activitiesByCc = ctx.Activities.Values.GroupBy(a => a.CostCenterId).ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToList());
        decimal noDriver = 0m;

        foreach (var cc in ctx.CostCenters)
        {
            var balance = support.CcFinal.GetValueOrDefault(cc.Id) * multiplier;
            if (balance <= Tolerance) continue;

            var rule = ctx.Rules.FirstOrDefault(r => r.Stage == 3 && r.SourceCostCenterId == cc.Id);
            var driver = rule?.DriverId is { } d && ctx.Drivers.TryGetValue(d, out var di) ? di : null;

            var candidates = (activitiesByCc.GetValueOrDefault(cc.Id) ?? [])
                .Select(id => (Id: id, Qty: ActivityDriverQty(driver, usage, id)))
                .Where(a => a.Qty > 0m).ToList();
            var volume = candidates.Sum(a => a.Qty);

            if (volume <= 0m)
            {
                // FR-3.13: no measured volume, so the balance stays visible in a named bucket.
                noDriver += balance;
                result.NoDriverDetail.Add(new { costCenter = cc.Name, amount = Money.R4(balance) });
                output.Allocations.Add(new AllocationResult
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, RuleId = rule?.Id, SourcePoolId = rule?.SourcePoolId, SourceCostCenterId = cc.Id,
                    TargetType = "RESIDUAL", TargetLabel = "No driver volume", Stage = 3, DriverId = driver?.Id, Amount = Money.R4(balance),
                    CalcExpression = $"{cc.Name}: no measured activity in the period, so SAR {Fmt.N(balance)} could not be attributed to any activity (NO_DRIVER_VOLUME).",
                });
                continue;
            }

            var parts = Money.Distribute(balance, candidates.Select(c => c.Qty).ToArray(), ctx.DistributionScale);
            for (var i = 0; i < candidates.Count; i++)
            {
                var portion = parts[i];
                if (portion == 0m) continue;
                var (id, qty) = candidates[i];
                result.ActivityOverhead[id] = result.ActivityOverhead.GetValueOrDefault(id) + portion;
                var act = ctx.Activities[id];
                output.Allocations.Add(new AllocationResult
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, RuleId = rule?.Id, SourcePoolId = rule?.SourcePoolId, SourceCostCenterId = cc.Id,
                    TargetType = "ACTIVITY", TargetCostCenterId = cc.Id, TargetActivityId = id, TargetLabel = act.Name, Stage = 3,
                    DriverId = driver?.Id, DriverQty = Money.R6(qty), DriverVolume = Money.R6(volume), DriverShare = Money.R6(qty / volume),
                    Amount = Money.R4(portion),
                    CalcExpression = IsMeasured(driver)
                        ? $"{cc.Name} balance SAR {Fmt.N(balance)} x ({Fmt.N(qty)} / {Fmt.N(volume)} {driver!.Unit}) = SAR {Fmt.N(portion)} to {act.Name}"
                        : $"{cc.Name} balance SAR {Fmt.N(balance)} allocated value-based{(driver is null ? "" : $" (structural driver {driver.Name} has no activity-level measurement)")}: " +
                          $"{act.Name} carries {Fmt.P(qty / volume * 100m, 2)}% of the centre's direct resource cost = SAR {Fmt.N(portion)}",
                });
            }
        }

        // ── Push activity overhead down to consuming services ─────────────────────
        var servicesByActivity = new Dictionary<Guid, List<(Guid ServiceId, decimal Cost)>>();
        foreach (var su in usage.ByService.Values)
            foreach (var (activityId, cost) in su.ByActivity)
            {
                if (!servicesByActivity.TryGetValue(activityId, out var list)) servicesByActivity[activityId] = list = [];
                list.Add((su.Service.Id, cost));
            }

        decimal orphaned = 0m;
        foreach (var (activityId, amount) in result.ActivityOverhead)
        {
            var consumers = servicesByActivity.GetValueOrDefault(activityId) ?? [];
            var total = consumers.Sum(c => c.Cost);
            if (consumers.Count == 0 || total <= 0m)
            {
                // FR-3.15: overhead with no patient consumer is NON_PATIENT_ACTIVITY.
                orphaned += amount;
                result.OrphanDetail.Add(new { activity = ctx.Activities.TryGetValue(activityId, out var a) ? a.Name : activityId.ToString(), amount = Money.R4(amount) });
                continue;
            }
            var parts = Money.Distribute(amount, consumers.Select(c => c.Cost).ToArray(), ctx.DistributionScale);
            for (var i = 0; i < consumers.Count; i++)
                if (parts[i] != 0m) result.ServiceOverhead[consumers[i].ServiceId] = result.ServiceOverhead.GetValueOrDefault(consumers[i].ServiceId) + parts[i];
        }

        // ── Activity cost cards ───────────────────────────────────────────────────
        foreach (var act in ctx.Activities.Values)
        {
            usage.ByActivity.TryGetValue(act.Id, out var au);
            var overhead = Money.R4(result.ActivityOverhead.GetValueOrDefault(act.Id));
            var direct = Money.R4(au?.ResourceCost ?? 0m);
            var volume = Money.R6(au?.Occurrences ?? 0m);
            var total = direct + overhead;
            if (total == 0m && volume == 0m) continue;
            var unit = volume > 0m ? total / volume : 0m;
            output.ActivityCosts.Add(new ActivityCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, ActivityId = act.Id, Volume = volume,
                DirectCost = direct, OverheadCost = overhead, TotalCost = total, UnitCost = Money.R6(unit),
                CalcExpression = $"direct resource cost SAR {Fmt.N(direct)} + allocated overhead SAR {Fmt.N(overhead)} = SAR {Fmt.N(total)} " +
                                 $"over {Fmt.N(volume)} occurrences = SAR {Fmt.N(unit)} per occurrence",
            });
        }

        result.OrphanedOverhead = Money.R4(orphaned);
        result.NoDriverVolume = Money.R4(noDriver);
        var allocated = result.ActivityOverhead.Values.Sum();
        var moneyIn = support.CcFinal.Values.Where(v => v > 0m).Sum() * multiplier;

        return (result, new StepResult(
            output.Allocations.Count - startRows + output.ActivityCosts.Count, Money.R4(moneyIn), Money.R4(allocated + noDriver),
            noDriver > 1m || orphaned > 1m ? "WARNING" : "SUCCESS",
            $"{output.ActivityCosts.Count} activities costed; {Fmt.Sar(allocated)} of overhead attributed" +
            (noDriver > 1m ? $", {Fmt.Sar(noDriver)} held back for want of driver volume" : "") +
            (orphaned > 1m ? $", {Fmt.Sar(orphaned)} on activities with no patient consumer" : "") + ".",
            new Dictionary<string, object?>
            {
                ["activities"] = output.ActivityCosts.Count, ["allocated"] = Money.R4(allocated),
                ["noDriverVolume"] = Money.R4(noDriver), ["orphaned"] = Money.R4(orphaned),
            }));
    }
}
