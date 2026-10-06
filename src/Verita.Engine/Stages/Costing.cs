using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

public sealed record StdComponent(decimal Quantity, decimal Cost, string Uom, string Label);

public sealed class StandardCostResult
{
    /// <summary>procedureId -> standard unit cost under the run's costing version.</summary>
    public Dictionary<Guid, decimal> StandardUnit { get; } = [];
    /// <summary>(procedure, component type) -> standard quantity and cost per procedure unit.</summary>
    public Dictionary<(Guid ProcedureId, string Type), StdComponent> Components { get; } = [];
}

public sealed record EncounterTotals(
    decimal DirectCost, decimal IndirectCost, decimal OverheadCost, decimal TotalCost, decimal StandardCost,
    decimal Revenue, decimal Los, int ServiceCount);

public sealed class ActualCostResult
{
    public Dictionary<Guid, EncounterTotals> EncounterTotals { get; } = [];
    /// <summary>(procedure, component type) -> measured quantity and cost across the period.</summary>
    public Dictionary<(Guid ProcedureId, string Type), (decimal Quantity, decimal Cost, string Uom)> Components { get; } = [];
    public decimal PatientCostTotal { get; set; }
    public decimal RevenueTotal { get; set; }
}

/// <summary>Stages 7 and 8 — standard and actual cost (FR-5).</summary>
public static class Costing
{
    /// <summary>Amount = ROUND(Quantity x UnitCost, 4) with both inputs at six places — the
    /// identity the database enforces on CostComponent with a check constraint.</summary>
    private static (decimal Q, decimal U, decimal A) Line(decimal quantity, decimal unitCost)
    {
        var q = Money.R6(quantity);
        var u = Money.R6(unitCost);
        return (q, u, Money.R4(q * u));
    }

    /// <summary>
    /// Stage 7 — what a procedure should cost: its recipe priced at the effective rates plus the
    /// ABC overhead each activity carries. The overhead rate per occurrence comes from this run's
    /// own allocation, so standard and actual overhead share a denominator (FR-5.2).
    /// </summary>
    public static (StandardCostResult Result, StepResult Step) Standard(RunContext ctx, UsageIndex usage, ActivityAllocationResult alloc, StageOutput output)
    {
        var result = new StandardCostResult();
        var startComponents = output.Components.Count;

        var overheadPerOccurrence = new Dictionary<Guid, decimal>();
        foreach (var (activityId, amount) in alloc.ActivityOverhead)
        {
            var occ = usage.ByActivity.TryGetValue(activityId, out var au) ? au.Occurrences : 0m;
            if (occ > 0m) overheadPerOccurrence[activityId] = amount / occ;
        }

        foreach (var proc in ctx.Procedures.Values)
        {
            if (proc.ActivityLines.Count == 0 && proc.ResourceLines.Count == 0) continue;

            var byResource = new Dictionary<Guid, (decimal Quantity, string Uom, Guid? ActivityId)>();
            decimal overhead = 0m;
            var overheadParts = new List<string>();

            foreach (var line in proc.ActivityLines)
            {
                if (!ctx.Activities.TryGetValue(line.ActivityId, out var act)) continue;
                foreach (var r in act.Recipe)
                {
                    if (!ctx.Resources.ContainsKey(r.ResourceId)) continue;
                    var q = r.StdQuantity * line.Quantity;
                    byResource[r.ResourceId] = byResource.TryGetValue(r.ResourceId, out var e) ? e with { Quantity = e.Quantity + q } : (q, r.Uom, act.Id);
                }
                var perOcc = overheadPerOccurrence.GetValueOrDefault(act.Id);
                if (perOcc > 0m)
                {
                    overhead += perOcc * line.Quantity;
                    overheadParts.Add($"{act.Name} {Fmt.N(line.Quantity)} x {Fmt.N(perOcc)}");
                }
            }
            foreach (var line in proc.ResourceLines)
            {
                if (!ctx.Resources.ContainsKey(line.ResourceId)) continue;
                byResource[line.ResourceId] = byResource.TryGetValue(line.ResourceId, out var e) ? e with { Quantity = e.Quantity + line.Quantity } : (line.Quantity, line.Uom, null);
            }

            decimal direct = 0m, indirect = 0m;
            foreach (var (resourceId, v) in byResource)
            {
                var res = ctx.Resources[resourceId];
                var nature = Catalogue.NatureOf(res.ResourceType);
                var (q, u, a) = Line(v.Quantity, res.Rate);
                if (nature == "DIRECT") direct += a; else indirect += a;

                var key = (proc.Id, res.ResourceType);
                result.Components[key] = result.Components.TryGetValue(key, out var agg)
                    ? agg with { Quantity = agg.Quantity + v.Quantity, Cost = agg.Cost + a }
                    : new StdComponent(v.Quantity, a, v.Uom, Catalogue.ComponentLabel(res.ResourceType));

                output.Components.Add(new CostComponent
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, CostingVersionId = ctx.CostingVersionId, Scope = "PROCEDURE", ScopeId = proc.Id,
                    ProcedureId = proc.Id, ActivityId = v.ActivityId, ResourceId = resourceId, ComponentType = res.ResourceType, CostNature = nature,
                    Label = res.Name, Quantity = q, Uom = v.Uom, UnitCost = u, Amount = a,
                    CalcExpression = $"{Fmt.N(q, 4)} {v.Uom} x SAR {Fmt.R(u)} per {v.Uom} = SAR {Fmt.N(a)} (standard recipe, {ctx.CostingVersionCode}, rate effective {Fmt.Date(ctx.AsOf)})",
                    SourceSystem = "VERITA-STANDARD", SourceRef = $"{proc.Code}/{res.Code}", CalculatedAt = ctx.CalculatedAt,
                });
            }

            decimal overheadAmount = 0m;
            if (overhead > 0m)
            {
                var (q, u, a) = Line(1m, overhead);
                overheadAmount = a;
                result.Components[(proc.Id, "OVERHEAD")] = new StdComponent(1m, a, "UNIT", "Allocated overhead");
                output.Components.Add(new CostComponent
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, CostingVersionId = ctx.CostingVersionId, Scope = "PROCEDURE", ScopeId = proc.Id,
                    ProcedureId = proc.Id, ComponentType = "OVERHEAD", CostNature = "OVERHEAD", Label = "Allocated overhead",
                    Quantity = q, Uom = "UNIT", UnitCost = u, Amount = a,
                    CalcExpression = $"ABC overhead per activity occurrence from this run's allocation: {string.Join(" + ", overheadParts.Take(6))}{(overheadParts.Count > 6 ? " + …" : "")} = SAR {Fmt.N(a)}",
                    SourceSystem = "VERITA-ABC", SourceRef = $"{proc.Code}/OVERHEAD", CalculatedAt = ctx.CalculatedAt,
                });
            }

            var total = direct + indirect + overheadAmount;
            result.StandardUnit[proc.Id] = total;
            output.ProcedureCosts.Add(new ProcedureCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, ProcedureId = proc.Id, CostType = "STANDARD",
                Volume = Money.R6(usage.ProcedureVolume.GetValueOrDefault(proc.Id)),
                DirectCost = direct, IndirectCost = indirect, OverheadCost = overheadAmount, TotalCost = total, UnitCost = total,
            });
        }

        var written = output.Components.Count - startComponents;
        var procs = output.ProcedureCosts.Count(p => p.CostType == "STANDARD");
        return (result, new StepResult(procs + written, 0m, 0m, "SUCCESS",
            $"{procs} procedures costed at standard under {ctx.CostingVersionCode}; {written} cost components written.",
            new Dictionary<string, object?> { ["procedures"] = procs, ["components"] = written }));
    }

    /// <summary>
    /// Stage 8 — what it actually cost, per encounter (FR-5.4, FR-5.7). Every term traces to a
    /// measured transaction carrying its source system and reference (FR-5.5).
    /// </summary>
    public static (ActualCostResult Result, StepResult Step) Actual(RunContext ctx, UsageIndex usage, ActivityAllocationResult alloc, StandardCostResult standard, CapacityResult capacity, StageOutput output)
    {
        var result = new ActualCostResult();
        var o = ctx.Overrides;
        var startComponents = output.Components.Count;

        var revenueByEncounter = ctx.Revenue.GroupBy(r => r.EncounterId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.NetAmount * o.Get(o.Reimbursement, r.PayerId) * o.PatientVolume));
        var servicesByEncounter = ctx.Services.GroupBy(s => s.EncounterId).ToDictionary(g => g.Key, g => g.ToList());
        var procActual = new Dictionary<Guid, (decimal Direct, decimal Indirect, decimal Overhead, decimal Volume, decimal Revenue)>();

        decimal patientCost = 0m, revenueTotal = 0m;

        foreach (var enc in ctx.Encounters)
        {
            if (!usage.ByEncounter.TryGetValue(enc.Id, out var eu)) continue;
            var services = servicesByEncounter.GetValueOrDefault(enc.Id) ?? [];

            decimal direct = 0m, indirect = 0m;
            foreach (var (resourceId, v) in eu.ByResource)
            {
                if (!ctx.Resources.TryGetValue(resourceId, out var res) || v.Cost == 0m) continue;
                var nature = Catalogue.NatureOf(res.ResourceType);
                var (q, u, a) = Line(v.Quantity, res.Rate);
                if (nature == "DIRECT") direct += a; else indirect += a;
                // One component per resource consumed: the transactions themselves are one join
                // away and carry the source references (FR-11.8).
                output.Components.Add(new CostComponent
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, CostingVersionId = ctx.CostingVersionId, Scope = "ENCOUNTER", ScopeId = enc.Id,
                    EncounterId = enc.Id, ResourceId = resourceId, CostPoolId = res.CostPoolId, ComponentType = res.ResourceType, CostNature = nature,
                    Label = res.Name, Quantity = q, Uom = v.Uom, UnitCost = u, Amount = a,
                    CalcExpression = $"{Fmt.N(q, 4)} {v.Uom} measured x SAR {Fmt.R(u)} per {v.Uom} = SAR {Fmt.N(a)}",
                    SourceSystem = "VIDA-HIS", SourceRef = $"encounter/{enc.Id}/resource/{res.Code}", CalculatedAt = ctx.CalculatedAt,
                });
            }

            var overheadRaw = eu.ServiceIds.Sum(id => alloc.ServiceOverhead.GetValueOrDefault(id));
            decimal overhead = 0m;
            if (overheadRaw > 0m)
            {
                var (q, u, a) = Line(1m, overheadRaw);
                overhead = a;
                output.Components.Add(new CostComponent
                {
                    Id = IdGen.Sequential(), RunId = ctx.RunId, CostingVersionId = ctx.CostingVersionId, Scope = "ENCOUNTER", ScopeId = enc.Id,
                    EncounterId = enc.Id, ComponentType = "OVERHEAD", CostNature = "OVERHEAD", Label = "Allocated overhead",
                    Quantity = q, Uom = "UNIT", UnitCost = u, Amount = a,
                    CalcExpression = $"Overhead allocated through {services.Count} service {Fmt.Plural(services.Count, "line")} from their activities' cost centres, pro rata to direct resource cost = SAR {Fmt.N(a)}",
                    SourceSystem = "VERITA-ABC", SourceRef = $"encounter/{enc.Id}/overhead", CalculatedAt = ctx.CalculatedAt,
                });
            }

            var total = direct + indirect + overhead;
            var los = enc.LengthOfStay * o.LosFactor;
            var revenue = revenueByEncounter.GetValueOrDefault(enc.Id);
            var standardCost = services.Sum(s => standard.StandardUnit.GetValueOrDefault(s.ProcedureId) * s.Quantity);
            patientCost += total;
            revenueTotal += revenue;

            result.EncounterTotals[enc.Id] = new EncounterTotals(direct, indirect, overhead, total, standardCost, revenue, los, services.Count);

            var contribution = revenue - direct - indirect;
            var operating = revenue - total;
            output.EncounterCosts.Add(new EncounterCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, EncounterId = enc.Id,
                DirectCost = direct, IndirectCost = indirect, OverheadCost = overhead, TotalCost = total,
                StandardCost = Money.R4(standardCost), Revenue = Money.R4(revenue),
                ContributionMargin = Money.R4(contribution), OperatingMargin = Money.R4(operating),
                MarginPct = revenue > 0m ? Money.R6(operating / revenue * 100m) : 0m,
                CostPerDay = Money.R4(total / Math.Max(los, 1m)), ServiceCount = services.Count,
                // FR-5.9: the encounter's weighted activity units, as measured by DRV-WAU.
                WeightedActivityUnits = Money.R6(1.0m * services.Count + 0.5m * los + (enc.IsSurgical ? 2.0m : 0m)),
            });

            foreach (var svc in services)
            {
                if (!usage.ByService.TryGetValue(svc.Id, out var su)) continue;
                var acc = procActual.GetValueOrDefault(svc.ProcedureId);
                procActual[svc.ProcedureId] = (acc.Direct + su.DirectCost, acc.Indirect + su.IndirectCost,
                    acc.Overhead + alloc.ServiceOverhead.GetValueOrDefault(svc.Id), acc.Volume + svc.Quantity * o.Get(o.ProcedureVolume, svc.ProcedureId) * o.PatientVolume, acc.Revenue);
            }
        }

        foreach (var r in ctx.Revenue.Where(r => r.ProcedureId is not null))
        {
            var pid = r.ProcedureId!.Value;
            if (procActual.TryGetValue(pid, out var acc))
                procActual[pid] = acc with { Revenue = acc.Revenue + r.NetAmount * o.Get(o.Reimbursement, r.PayerId) * o.PatientVolume };
        }

        foreach (var (k, v) in usage.ByProcedureComponent) result.Components[k] = v;
        // CR-6 / FR-6.5: allocated overhead must sit on the actual side too, or the whole of
        // standard overhead reads as a favourable variance.
        foreach (var (procedureId, v) in procActual)
            if (v.Overhead != 0m) result.Components[(procedureId, "OVERHEAD")] = (v.Volume, v.Overhead, "UNIT");

        foreach (var (procedureId, v) in procActual)
        {
            var total = v.Direct + v.Indirect + v.Overhead;
            output.ProcedureCosts.Add(new ProcedureCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, ProcedureId = procedureId, CostType = "ACTUAL",
                Volume = Money.R6(v.Volume), DirectCost = Money.R4(v.Direct), IndirectCost = Money.R4(v.Indirect), OverheadCost = Money.R4(v.Overhead),
                TotalCost = Money.R4(total), UnitCost = v.Volume > 0m ? Money.R6(total / v.Volume) : 0m, Revenue = Money.R4(v.Revenue),
                ContributionMargin = Money.R4(v.Revenue - v.Direct - v.Indirect), OperatingMargin = Money.R4(v.Revenue - total),
            });
        }

        result.PatientCostTotal = Money.R4(patientCost);
        result.RevenueTotal = Money.R4(revenueTotal);
        var written = output.Components.Count - startComponents;
        // FR-2.4: what should reach patients (measured consumption plus the overhead pushed to
        // services) against what the encounter cost records actually carry.
        var expected = Money.R4(capacity.TotalConsumed + alloc.ServiceOverhead.Values.Sum());
        return (result, new StepResult(output.EncounterCosts.Count + procActual.Count + written, expected, result.PatientCostTotal,
            Math.Abs(expected - result.PatientCostTotal) >= 1m ? "WARNING" : "SUCCESS",
            $"{output.EncounterCosts.Count} encounters costed at {Fmt.Sar(patientCost)} against {Fmt.Sar(revenueTotal)} of revenue.",
            new Dictionary<string, object?>
            {
                ["encounters"] = output.EncounterCosts.Count, ["patientCost"] = result.PatientCostTotal,
                ["revenue"] = result.RevenueTotal, ["components"] = written,
            }));
    }
}
