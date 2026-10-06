using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

/// <summary>
/// Stage 9 — package and DRG roll-ups (FR-5.11 to FR-5.14). Department, physician, payer and
/// service-line profitability are deliberately not materialised: they are group-bys over
/// EncounterCost computed at read time (FR-5.16). Packages and DRGs get tables because they need
/// percentiles and outlier counts.
/// </summary>
public static class Aggregate
{
    public static StepResult Run(RunContext ctx, ActualCostResult actual, StandardCostResult standard, StageOutput output)
    {
        // ── Packages ──────────────────────────────────────────────────────────────
        var packageStandard = ctx.Packages.Values.ToDictionary(
            p => p.Id,
            p => p.Components.Sum(c => standard.StandardUnit.GetValueOrDefault(c.ProcedureId) * c.Quantity));

        foreach (var g in ctx.Encounters.Where(e => e.PackageId is not null && actual.EncounterTotals.ContainsKey(e.Id)).GroupBy(e => e.PackageId!.Value))
        {
            var cases = g.Count();
            var actualCost = g.Sum(e => actual.EncounterTotals[e.Id].TotalCost);
            var revenue = g.Sum(e => actual.EncounterTotals[e.Id].Revenue);
            var direct = g.Sum(e => actual.EncounterTotals[e.Id].DirectCost + actual.EncounterTotals[e.Id].IndirectCost);
            var standardTotal = packageStandard.GetValueOrDefault(g.Key) * cases;
            var variance = actualCost - standardTotal;
            output.PackageCosts.Add(new PackageCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, PackageId = g.Key, CaseCount = cases,
                StandardCost = Money.R4(standardTotal), ActualCost = Money.R4(actualCost), Revenue = Money.R4(revenue),
                Variance = Money.R4(variance), VariancePct = standardTotal > 0m ? Money.R6(variance / standardTotal * 100m) : 0m,
                ContributionMargin = Money.R4(revenue - direct), OperatingMargin = Money.R4(revenue - actualCost),
                MarginPct = revenue > 0m ? Money.R6((revenue - actualCost) / revenue * 100m) : 0m,
            });
        }

        // ── DRGs ──────────────────────────────────────────────────────────────────
        foreach (var g in ctx.Encounters.Where(e => e.DrgId is not null && actual.EncounterTotals.ContainsKey(e.Id)).GroupBy(e => e.DrgId!.Value))
        {
            var costs = g.Select(e => actual.EncounterTotals[e.Id].TotalCost).ToArray();
            var los = g.Select(e => (decimal)e.LengthOfStay).ToArray();
            var revenue = g.Select(e => actual.EncounterTotals[e.Id].Revenue).ToArray();
            var avg = Money.Mean(costs);
            var sd = Money.StdDev(costs);
            var p25 = Money.Percentile(costs, 25m);
            var p75 = Money.Percentile(costs, 75m);
            // FR-5.13: Tukey's rule on the IQR, not a standard-deviation threshold — hospital
            // cost distributions are right-skewed.
            var fence = p75 + 1.5m * (p75 - p25);
            var avgLos = Money.Mean(los);
            var avgRev = Money.Mean(revenue);
            output.DrgSummaries.Add(new DrgCostSummary
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, DrgId = g.Key, CaseCount = costs.Length,
                AvgCost = Money.R4(avg), MedianCost = Money.R4(Money.Percentile(costs, 50m)), P25Cost = Money.R4(p25), P75Cost = Money.R4(p75),
                P90Cost = Money.R4(Money.Percentile(costs, 90m)), MinCost = Money.R4(costs.Min()), MaxCost = Money.R4(costs.Max()),
                StdDevCost = Money.R4(sd), AvgLOS = Money.R6(avgLos), CostPerDay = Money.R4(avg / Math.Max(avgLos, 1m)),
                AvgReimbursement = Money.R4(avgRev), AvgMargin = Money.R4(avgRev - avg),
                MarginPct = avgRev > 0m ? Money.R6((avgRev - avg) / avgRev * 100m) : 0m,
                VariationCoeff = avg > 0m ? Money.R6(sd / avg) : 0m, OutlierCount = costs.Count(c => c > fence),
            });
        }

        var lossMaking = output.PackageCosts.Count(p => p.OperatingMargin < 0m);
        return new StepResult(output.PackageCosts.Count + output.DrgSummaries.Count, 0m, 0m, "SUCCESS",
            $"{output.PackageCosts.Count} packages and {output.DrgSummaries.Count} DRGs summarised" +
            (lossMaking > 0 ? $"; {lossMaking} {Fmt.Plural(lossMaking, "package is", "packages are")} loss-making." : "."),
            new Dictionary<string, object?>
            {
                ["packages"] = output.PackageCosts.Count, ["drgs"] = output.DrgSummaries.Count, ["lossMakingPackages"] = lossMaking,
                ["drgOutliers"] = output.DrgSummaries.Sum(d => d.OutlierCount),
            });
    }
}
