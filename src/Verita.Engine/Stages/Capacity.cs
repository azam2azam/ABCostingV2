using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

public sealed record ResidualEntry(string Bucket, string Label, decimal Amount, string Explanation, object? Detail);

public sealed class CapacityResult
{
    public List<ResidualEntry> Residuals { get; } = [];
    public Dictionary<Guid, decimal> PoolConsumed { get; } = [];
    /// <summary>poolId -> consumed + idle + wastage - over-capacity: what the model says the pool paid for.</summary>
    public Dictionary<Guid, decimal> PoolAccounted { get; } = [];
    public decimal TotalConsumed { get; set; }
    public decimal TotalUnused { get; set; }
}

/// <summary>
/// Stage 4 — time-driven capacity, unit costs and the cost of idleness (FR-4).
///
/// Idle capacity is carved out before anything is allocated to a patient (CR-12). A hospital
/// running its theatres at 62% has a real cost of doing so; charging it to the cases performed
/// makes those cases look expensive for a reason that has nothing to do with them.
///
/// Supply resources follow the same logic with a different unit: the pool's supply balance over
/// the acquisition price is what was purchased, measured issues are what was consumed, and the
/// difference is wastage, expiry and stock movement (FR-4.8).
/// </summary>
public static class Capacity
{
    private static bool IsCapacityResource(ResInfo r) => r.IsCapacityDriven && Catalogue.CapacityDrivenTypes.Contains(r.ResourceType);

    public static (CapacityResult Result, StepResult Step) Compute(RunContext ctx, UsageIndex usage, PoolBuildResult pools, StageOutput output)
    {
        var result = new CapacityResult();
        var periodFraction = (decimal)ctx.PeriodDays / 365m;

        var unusedDetail = new List<(decimal Amount, object Row)>();
        var overDetail = new List<(decimal Amount, object Row)>();
        var wasteDetail = new List<(decimal Amount, object Row)>();
        decimal unusedTotal = 0m, overTotal = 0m, wasteTotal = 0m, consumedTotal = 0m;

        // FR-4.9 / CR-4: a pool funding both kinds (a blood bank's technologists and its blood)
        // has its capacity side costed first; only the remainder can be supply.
        var poolCapacityCost = new Dictionary<Guid, decimal>();
        foreach (var res in ctx.Resources.Values)
        {
            if (!IsCapacityResource(res) || res.Capacity is null || res.CostPoolId is not { } poolId) continue;
            poolCapacityCost[poolId] = poolCapacityCost.GetValueOrDefault(poolId) + res.Rate * res.Capacity.PracticalMinutes * periodFraction;
        }

        // A supply pool is shared by several resources, so its purchased value is split in
        // proportion to what each one consumed: the ledger has only a pool balance.
        var poolSupplyConsumed = new Dictionary<Guid, decimal>();
        foreach (var (resourceId, u) in usage.ByResource)
        {
            if (!ctx.Resources.TryGetValue(resourceId, out var res) || IsCapacityResource(res) || res.CostPoolId is not { } poolId) continue;
            poolSupplyConsumed[poolId] = poolSupplyConsumed.GetValueOrDefault(poolId) + u.Cost;
        }

        foreach (var res in ctx.Resources.Values)
        {
            var u = usage.ByResource.GetValueOrDefault(res.Id) ?? new ResourceUsage();
            var row = new ResourceUnitCost { Id = IdGen.Sequential(), RunId = ctx.RunId, ResourceId = res.Id, Uom = res.Uom };

            if (IsCapacityResource(res) && res.Capacity is { } cap)
            {
                // FR-4.1/4.2: the denominator is practical capacity, never theoretical.
                var available = cap.PracticalMinutes * periodFraction;
                var used = u.Quantity;
                var unused = available - used;
                var rate = res.Rate;
                var costOfUsed = Money.R4(rate * used);
                var costOfUnused = Money.R4(rate * unused);

                consumedTotal += costOfUsed;
                if (res.CostPoolId is { } poolId)
                {
                    result.PoolConsumed[poolId] = result.PoolConsumed.GetValueOrDefault(poolId) + costOfUsed;
                    result.PoolAccounted[poolId] = result.PoolAccounted.GetValueOrDefault(poolId) + costOfUsed + costOfUnused;
                }

                // FR-4.7 / CR-8: the stored percentage derives from the stored, rounded figures.
                var availableR = Money.R6(available);
                var usedR = Money.R6(used);
                var utilisation = Money.Pct(usedR, availableR);

                if (unused >= 0m)
                {
                    unusedTotal += costOfUnused;
                    if (costOfUnused > 0m)
                        unusedDetail.Add((costOfUnused, new { resource = res.Name, code = res.Code, unusedMinutes = Money.R2(unused), amount = costOfUnused, utilisationPct = utilisation }));
                }
                else
                {
                    // FR-4.6: over-capacity is legal and reported apart from idle capacity —
                    // idle theatres and overtime call for opposite management responses.
                    overTotal += -costOfUnused;
                    overDetail.Add((-costOfUnused, new { resource = res.Name, code = res.Code, excessMinutes = Money.R2(-unused), amount = -costOfUnused, utilisationPct = utilisation }));
                }

                row.AnnualCost = cap.AnnualCost;
                row.PeriodCost = Money.R4(rate * available);
                row.TheoreticalMinutes = Money.R6(cap.TheoreticalMinutes);
                row.PracticalMinutes = Money.R6(cap.PracticalMinutes);
                row.AvailableMinutes = availableR;
                row.UsedMinutes = usedR;
                row.UnusedMinutes = Money.R6(unused);
                row.UnitCost = Money.R6(rate);
                row.CostOfUsedCapacity = costOfUsed;
                row.CostOfUnusedCapacity = Math.Max(costOfUnused, 0m);
                row.UtilisationPct = utilisation;
                row.CalcExpression =
                    $"{Fmt.N(cap.AnnualCost, 0)} annual / {Fmt.N(cap.PracticalMinutes, 0)} practical {res.Uom} " +
                    $"({Fmt.N(cap.TheoreticalMinutes, 0)} theoretical x {Fmt.P(cap.PracticalRatio * 100m)}%) = {Fmt.R(rate)} SAR per {res.Uom}; " +
                    $"available {Fmt.N(available, 0)} = practical x {ctx.PeriodDays}/365, used {Fmt.N(used, 0)} ({Fmt.P(utilisation)}%)";
            }
            else
            {
                var poolBalance = res.CostPoolId is { } pid
                    ? Math.Max(pools.PoolDirect.GetValueOrDefault(pid) - poolCapacityCost.GetValueOrDefault(pid), 0m)
                    : 0m;
                var poolConsumption = res.CostPoolId is { } pid2 ? poolSupplyConsumed.GetValueOrDefault(pid2) : 0m;
                var share = poolConsumption > 0m ? u.Cost / poolConsumption : 0m;
                var purchasedValue = poolBalance * share;
                var purchasedUnits = Money.R6(Money.SafeDiv(purchasedValue, res.Rate) ?? 0m);
                var wasted = purchasedValue - u.Cost;

                consumedTotal += u.Cost;
                if (res.CostPoolId is { } poolId)
                {
                    result.PoolConsumed[poolId] = result.PoolConsumed.GetValueOrDefault(poolId) + u.Cost;
                    result.PoolAccounted[poolId] = result.PoolAccounted.GetValueOrDefault(poolId) + u.Cost + Math.Max(wasted, 0m);
                }
                if (wasted > 0m)
                {
                    wasteTotal += wasted;
                    wasteDetail.Add((Money.R4(wasted), new { resource = res.Name, code = res.Code, wastedUnits = Money.R2(purchasedUnits - u.Quantity), amount = Money.R4(wasted), utilisationPct = Money.Pct(u.Quantity, purchasedUnits) }));
                }

                var usedR = Money.R6(u.Quantity);
                row.PeriodCost = Money.R4(purchasedValue);
                row.TheoreticalMinutes = purchasedUnits;
                row.PracticalMinutes = purchasedUnits;
                row.AvailableMinutes = purchasedUnits;
                row.UsedMinutes = usedR;
                row.UnusedMinutes = Money.R6(purchasedUnits - usedR);
                row.UnitCost = Money.R6(res.Rate);
                row.CostOfUsedCapacity = Money.R4(u.Cost);
                row.CostOfUnusedCapacity = Money.R4(Math.Max(wasted, 0m));
                row.UtilisationPct = Money.Pct(usedR, purchasedUnits);
                row.CalcExpression =
                    $"issued {Fmt.N(u.Quantity, 2)} {res.Uom} x {Fmt.R(res.Rate)} SAR = {Fmt.N(u.Cost, 2)}; " +
                    $"its share of the pool's supply balance implies {Fmt.N(purchasedUnits, 2)} {res.Uom} purchased, " +
                    $"{Fmt.N(purchasedUnits - u.Quantity, 2)} not reaching a patient";
            }
            output.UnitCosts.Add(row);
        }

        static object Top(List<(decimal Amount, object Row)> d) => d.OrderByDescending(x => x.Amount).Take(25).Select(x => x.Row).ToArray();

        if (unusedTotal > 0.00005m)
            result.Residuals.Add(new("UNUSED_CAPACITY", "Cost of unused capacity", Money.R4(unusedTotal),
                "Practical capacity that was paid for but not consumed by patient care. It is reported here rather than spread across cases, because a case is not more expensive for having been performed in an under-utilised theatre.",
                Top(unusedDetail)));
        if (overTotal > 0.00005m)
            result.Residuals.Add(new("OVER_CAPACITY", "Capacity consumed beyond the practical model", Money.R4(-overTotal),
                "Resources were used beyond their modelled practical capacity — overtime, extended lists or equipment run outside its service window. The cost has already reached patients, so it is added back, and it is never netted against idle capacity.",
                Top(overDetail)));
        if (wasteTotal > 0.00005m)
            result.Residuals.Add(new("NON_PATIENT_ACTIVITY", "Supply purchased but not issued to a patient", Money.R4(wasteTotal),
                "Drugs, consumables and blood products acquired in the period that did not reach patient care: wastage, expiry, breakage and stock movement. Supply chain owns this number.",
                Top(wasteDetail)));

        result.TotalConsumed = Money.R4(consumedTotal);
        result.TotalUnused = Money.R4(unusedTotal);

        var withCapacity = output.UnitCosts.Where(r => r.AvailableMinutes > 0m).ToArray();
        var avgUtil = withCapacity.Length == 0 ? 0m : withCapacity.Average(r => r.UtilisationPct);

        // FR-2.4: money in is the ledger balance of the consumption-stream pools; money out is what
        // the stage accounted for (consumed + idle + wastage - over-capacity). Where a pool's ledger
        // balance and its modelled capacity cost disagree, the gap shows here, at the stage that
        // owns it, named pool by pool.
        var consumptionPools = ctx.Pools.Where(p => !p.IsAllocationStream).Select(p => p.Id).ToHashSet();
        var ledgerIn = pools.PoolDirect.Where(kv => consumptionPools.Contains(kv.Key)).Sum(kv => kv.Value);
        var accounted = consumedTotal + unusedTotal + wasteTotal - overTotal;
        var poolGaps = pools.PoolDirect.Where(kv => consumptionPools.Contains(kv.Key))
            .Select(kv => (Pool: ctx.PoolById[kv.Key].Name, Gap: Money.R4(kv.Value - result.PoolAccounted.GetValueOrDefault(kv.Key))))
            .Where(x => Math.Abs(x.Gap) >= 0.01m).OrderByDescending(x => Math.Abs(x.Gap)).ToList();
        var gap = Money.R4(ledgerIn - accounted);
        return (result, new StepResult(output.UnitCosts.Count, Money.R4(ledgerIn), Money.R4(accounted),
            Math.Abs(gap) >= 1m ? "WARNING" : "SUCCESS",
            $"{output.UnitCosts.Count} resources costed; average utilisation {Fmt.P(avgUtil)}%." +
            (Math.Abs(gap) >= 0.01m
                ? $" Consumption-pool ledger balances differ from the modelled capacity cost by {Fmt.Sar(gap, 2)}: " +
                  string.Join(", ", poolGaps.Take(4).Select(x => $"{x.Pool} {Fmt.Sar(x.Gap, 2)}")) + "."
                : ""),
            new Dictionary<string, object?>
            {
                ["avgUtilisationPct"] = Money.R2(avgUtil), ["consumed"] = Money.R4(consumedTotal),
                ["unusedCapacity"] = Money.R4(unusedTotal), ["overCapacity"] = Money.R4(overTotal), ["supplyWastage"] = Money.R4(wasteTotal),
                ["ledgerGap"] = gap, ["poolGaps"] = poolGaps.Select(x => new { pool = x.Pool, gap = x.Gap }).ToArray(),
            }));
    }
}
