using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

/// <summary>
/// Stage 10 — explainable variance (FR-6). Every procedure variance is decomposed into three
/// effects that sum exactly to actual minus standard:
///
///     rate     = (actualRate - standardRate) x standardQty
///     quantity = (actualQty  - standardQty)  x standardRate
///     mix      = (actualRate - standardRate) x (actualQty - standardQty)
///
/// The joint term is isolated rather than folded into rate or quantity (FR-6.3). Quantities are
/// per unit of procedure so volume does not masquerade as consumption (FR-6.4).
/// </summary>
public static class VarianceStage
{
    public static StepResult Run(RunContext ctx, UsageIndex usage, StandardCostResult standard, ActualCostResult actual, StageOutput output)
    {
        var stdByProc = standard.Components.GroupBy(k => k.Key.ProcedureId).ToDictionary(g => g.Key, g => g.Select(x => x.Key.Type).ToHashSet());
        var actByProc = actual.Components.GroupBy(k => k.Key.ProcedureId).ToDictionary(g => g.Key, g => g.Select(x => x.Key.Type).ToHashSet());
        var startVariances = output.Variances.Count;
        decimal rateAll = 0m, qtyAll = 0m;

        foreach (var proc in ctx.Procedures.Values)
        {
            var volume = usage.ProcedureVolume.GetValueOrDefault(proc.Id);
            if (volume <= 0m) continue;
            var standardTotal = standard.StandardUnit.GetValueOrDefault(proc.Id) * volume;
            if (standardTotal <= 0m) continue;

            var types = new HashSet<string>(stdByProc.GetValueOrDefault(proc.Id) ?? []);
            types.UnionWith(actByProc.GetValueOrDefault(proc.Id) ?? []);
            if (types.Count == 0) continue;

            var variance = new Variance
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, Scope = "PROCEDURE", ScopeId = proc.Id, ScopeLabel = proc.Name, Baseline = "STANDARD",
            };
            var components = new List<VarianceComponent>();
            decimal actualTotal = 0m, rateTotal = 0m, qtyTotal = 0m, mixTotal = 0m;

            foreach (var type in types)
            {
                var std = standard.Components.GetValueOrDefault((proc.Id, type));
                var hasAct = actual.Components.TryGetValue((proc.Id, type), out var act);

                var standardQty = std?.Quantity ?? 0m;
                var standardCost = (std?.Cost ?? 0m) * volume;
                var standardRate = standardQty > 0m ? (std?.Cost ?? 0m) / standardQty : 0m;

                var actualQty = (hasAct ? act.Quantity : 0m) / volume;
                var actualCost = hasAct ? act.Cost : 0m;
                var actualRate = actualQty > 0m ? actualCost / volume / actualQty : 0m;
                actualTotal += actualCost;

                var rate = (actualRate - standardRate) * standardQty * volume;
                var quantity = (actualQty - standardQty) * standardRate * volume;
                var mix = (actualRate - standardRate) * (actualQty - standardQty) * volume;
                rateTotal += rate; qtyTotal += quantity; mixTotal += mix;

                if (standardCost == 0m && actualCost == 0m) continue;

                // AC-2: the three effects are stored so they sum exactly to the stored total.
                var rateR = Money.R4(rate);
                var qtyR = Money.R4(quantity);
                var totalR = Money.R4(rate + quantity + mix);
                var mixR = totalR - rateR - qtyR;
                var label = Catalogue.ComponentLabel(type);
                components.Add(new VarianceComponent
                {
                    Id = IdGen.Sequential(), VarianceId = variance.Id, ComponentType = type, Label = label,
                    ActualQty = Money.R6(actualQty), StandardQty = Money.R6(standardQty), ActualRate = Money.R6(actualRate), StandardRate = Money.R6(standardRate),
                    RateEffect = rateR, QuantityEffect = qtyR, MixEffect = mixR, TotalEffect = totalR,
                    Explanation = Explain(label, std?.Uom ?? (hasAct ? act.Uom : "unit"), actualQty, standardQty, actualRate, standardRate, rate, quantity, mix, volume),
                });
            }

            var amount = actualTotal - standardTotal;
            // FR-6.8: direction derives from the amount.
            variance.Direction = Math.Abs(amount) < 1m ? "NEUTRAL" : amount > 0m ? "UNFAVOURABLE" : "FAVOURABLE";
            variance.ActualAmount = Money.R4(actualTotal);
            variance.BaselineAmount = Money.R4(standardTotal);
            variance.VarianceAmount = Money.R4(amount);
            variance.VariancePct = Money.R6(amount / standardTotal * 100m);
            variance.CaseCount = (int)Math.Round(volume, MidpointRounding.AwayFromZero);

            components.Sort((a, b) => Math.Abs(b.TotalEffect).CompareTo(Math.Abs(a.TotalEffect)));
            variance.Summary = Summarise(proc.Name, amount, volume, components.Take(3).ToList(), rateTotal, qtyTotal);
            rateAll += rateTotal; qtyAll += qtyTotal;

            output.Variances.Add(variance);
            output.VarianceComponents.AddRange(components);
        }

        var made = output.Variances.Skip(startVariances).ToList();
        var unfavourable = made.Count(v => v.Direction == "UNFAVOURABLE");
        var largest = made.OrderByDescending(v => Math.Abs(v.VarianceAmount)).FirstOrDefault();
        return new StepResult(made.Count + output.VarianceComponents.Count, 0m, 0m, "SUCCESS",
            $"{made.Count} procedure variances decomposed; {unfavourable} unfavourable" +
            (largest is null ? "." : $". Largest: {largest.ScopeLabel} at {Fmt.Sar(largest.VarianceAmount)}."),
            new Dictionary<string, object?>
            {
                ["variances"] = made.Count, ["unfavourable"] = unfavourable, ["rateEffectTotal"] = Money.R4(rateAll), ["quantityEffectTotal"] = Money.R4(qtyAll),
            });
    }

    private static string Explain(string label, string uom, decimal actualQty, decimal standardQty, decimal actualRate, decimal standardRate,
        decimal rate, decimal quantity, decimal mix, decimal volume)
    {
        var total = rate + quantity + mix;
        if (Math.Abs(total) < 1m) return $"{label}: in line with standard.";
        var dir = total > 0m ? "above" : "below";
        var parts = new List<string>();
        var cases = (int)Math.Round(volume, MidpointRounding.AwayFromZero);
        if (Math.Abs(quantity) >= 1m)
        {
            var delta = actualQty - standardQty;
            parts.Add($"SAR {Fmt.N(Math.Abs(quantity), 0)} from consumption ({Fmt.N(actualQty)} against {Fmt.N(standardQty)} {uom} per case, {(delta > 0 ? "+" : "")}{Fmt.N(delta)} across {cases} cases)");
        }
        if (Math.Abs(rate) >= 1m)
            parts.Add($"SAR {Fmt.N(Math.Abs(rate), 0)} from rate (SAR {Fmt.N(actualRate, 4)} against SAR {Fmt.N(standardRate, 4)} per {uom})");
        if (Math.Abs(mix) >= 1m)
            parts.Add($"SAR {Fmt.N(Math.Abs(mix), 0)} from rate and quantity moving together");
        var all = parts.Count == 1 && Math.Abs(quantity) >= Math.Abs(total) * 0.98m ? "all of it " : "";
        return $"{label}: {Fmt.N(Math.Abs(total), 0)} {dir} standard — {all}{string.Join(", ", parts)}.";
    }

    private static string Summarise(string name, decimal variance, decimal volume, List<VarianceComponent> top, decimal rate, decimal quantity)
    {
        if (Math.Abs(variance) < 1m) return $"{name} is costing what it should.";
        var dir = variance > 0m ? "above" : "below";
        var perCase = variance / Math.Max(volume, 1m);
        var drivers = top.Where(c => Math.Abs(c.TotalEffect) >= 1m).Select(c => $"SAR {Fmt.N(Math.Abs(c.TotalEffect), 0)} {c.Label.ToLowerInvariant()}").ToList();
        var dominant = Math.Abs(quantity) > Math.Abs(rate) * 1.5m ? "driven mainly by consumption"
            : Math.Abs(rate) > Math.Abs(quantity) * 1.5m ? "driven mainly by price"
            : "driven by a mix of price and consumption";
        return $"{name} ran SAR {Fmt.N(Math.Abs(variance), 0)} {dir} standard across {(int)Math.Round(volume, MidpointRounding.AwayFromZero)} cases " +
               $"(SAR {Fmt.N(Math.Abs(perCase), 0)} per case), {dominant}" + (drivers.Count > 0 ? $": {string.Join(", ", drivers)}." : ".");
    }
}
