using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine;

public sealed record ModelEncounter(Guid Id, Guid DepartmentId, Guid? ServiceLineId, Guid? ProviderId, Guid PayerId, Guid? DrgId, Guid? PackageId,
    string EncType, string Segment, int LengthOfStay);
public sealed record ModelLine(Guid EncounterId, Guid ProcedureId, Guid CostCenterId, Guid? ProviderId, decimal Quantity, string SourceRef);
public sealed record ModelRevenue(Guid EncounterId, string SourceRef, decimal Net);
public sealed record ModelProcedure(Guid Id, string Name, bool Medicine, string ProcType, decimal Price);
public sealed record ModelEncounterOverhead(decimal Pct, decimal Amount);

/// <summary>Everything a costing-model run reads, loaded once by the infrastructure layer (ARCH-1).</summary>
public sealed class CostingModelContext
{
    public required Guid RunId { get; init; }
    public required Guid HospitalId { get; init; }
    public required string HospitalName { get; init; }
    public required string PeriodCode { get; init; }
    public required string CostingVersionCode { get; init; }
    public required DateTime CalculatedAt { get; init; }
    public required List<ModelEncounter> Encounters { get; init; }
    public required List<ModelLine> Lines { get; init; }
    public required List<ModelRevenue> Revenue { get; init; }
    public required Dictionary<Guid, ModelProcedure> Procedures { get; init; }
    public required Dictionary<string, decimal> DirectTypes { get; init; }
    public required Dictionary<Guid, decimal> DirectItems { get; init; }
    public required Dictionary<string, decimal> IndirectTypes { get; init; }
    public required Dictionary<Guid, decimal> IndirectItems { get; init; }
    public required List<ModelEncounterOverhead> EncounterOverheads { get; init; }
    public required Dictionary<Guid, Guid?> DoctorResource { get; init; }
    /// <summary>Resources listed on the Resources screen; each gets a row even with nothing billed.</summary>
    public required Dictionary<Guid, string> ListedResources { get; init; }
    /// <summary>Display names by dimension, then by key.</summary>
    public required Dictionary<string, Dictionary<string, string>> Labels { get; init; }
}

/// <summary>
/// The run for a hospital costed by its costing model: a procedure costs its contracted price ×
/// Margin Cost %, plus contracted price × Overhead %, and every encounter carries its encounter
/// level overheads. Everything the read screens show is prepared and stored here.
/// </summary>
public sealed class CostingModelPipeline(IStageSink sink)
{
    public const string Method = "COSTING_MODEL";

    private sealed class Totals
    {
        public HashSet<Guid> Encounters { get; } = [];
        public decimal Revenue, Direct, Indirect, Overhead;
    }

    private async Task<T> RunStage<T>(string code, Func<StageOutput, (T Value, StepResult Step)> body, CancellationToken ct)
    {
        var def = Catalogue.CostingModelRunSteps.First(s => s.Code == code);
        await sink.StageStartedAsync(def, ct);
        var started = DateTime.UtcNow;
        var rows = new StageOutput();
        (T Value, StepResult Step) r;
        try
        {
            r = body(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sink.StageFailedAsync(def, ex.Message, DateTime.UtcNow - started, ct);
            throw new RunFailedException(ex.Message, code);
        }
        await sink.StageCompletedAsync(def, r.Step, rows, DateTime.UtcNow - started, ct);
        if (r.Step.Status == "FAILED") throw new RunFailedException(r.Step.Message ?? $"{def.Name} failed.", code);
        return r.Value;
    }

    private static string TypeKey(ModelProcedure p) =>
        p.Medicine ? "MEDICINE" : string.IsNullOrWhiteSpace(p.ProcType) ? "UNSPECIFIED" : p.ProcType;

    /// <summary>A procedure's own %, else its type's, else All's. Zero counts as not set.</summary>
    private static decimal? Pct(ModelProcedure p, Dictionary<Guid, decimal> items, Dictionary<string, decimal> types)
    {
        decimal? pct = items.TryGetValue(p.Id, out var item) ? item
            : types.TryGetValue(TypeKey(p), out var type) ? type
            : types.TryGetValue("ALL", out var all) ? all : null;
        return pct is 0m ? null : pct;
    }

    private static decimal UnitOf(ModelProcedure? p, Dictionary<Guid, decimal> items, Dictionary<string, decimal> types) =>
        p is not null && Pct(p, items, types) is { } pct ? Money.R4(p.Price * pct / 100m) : 0m;

    private static decimal OverheadOf(CostingModelContext ctx, decimal billed) => Money.R4(ctx.EncounterOverheads.Sum(o =>
    {
        if (o.Pct == 0m) return o.Amount;
        var share = billed * o.Pct / 100m;
        return o.Amount > 0m ? Math.Min(share, o.Amount) : share;
    }));

    /// <summary>INV-2-7-1 and REV-2-7-1 are the same invoice line.</summary>
    private static string LineRef(string sourceRef) =>
        sourceRef.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) || sourceRef.StartsWith("REV-", StringComparison.OrdinalIgnoreCase)
            ? sourceRef[4..] : sourceRef;

    public async Task<PipelineOutcome> ExecuteAsync(CostingModelContext ctx, CancellationToken ct)
    {
        await sink.StatusAsync("VALIDATING", ct);
        var billedProcedures = ctx.Lines.Select(l => l.ProcedureId).Distinct().ToList();
        var issues = await RunStage("VALIDATE", rows => Validate(ctx, billedProcedures, rows), ct);
        await sink.StatusAsync("VALIDATED", ct);
        await sink.StatusAsync("CALCULATING", ct);

        var linesByEncounter = ctx.Lines.GroupBy(l => l.EncounterId).ToDictionary(g => g.Key, g => g.ToList());
        var direct = await RunStage("DIRECT_COST", _ =>
        {
            var units = ctx.Procedures.Values.ToDictionary(p => p.Id, p => UnitOf(p, ctx.DirectItems, ctx.DirectTypes));
            var total = ctx.Lines.Sum(l => l.Quantity * units.GetValueOrDefault(l.ProcedureId));
            return (units, new StepResult(0, 0m, 0m, "SUCCESS", $"Direct costing under {ctx.CostingVersionCode} comes to {Fmt.Sar(total)}.",
                new Dictionary<string, object?> { ["direct"] = Money.R4(total) }));
        }, ct);
        var indirect = await RunStage("INDIRECT_COST", _ =>
        {
            var units = ctx.Procedures.Values.ToDictionary(p => p.Id, p => UnitOf(p, ctx.IndirectItems, ctx.IndirectTypes));
            var total = ctx.Lines.Sum(l => l.Quantity * units.GetValueOrDefault(l.ProcedureId));
            return (units, new StepResult(0, 0m, 0m, "SUCCESS", $"Procedure/activity overheads under {ctx.CostingVersionCode} come to {Fmt.Sar(total)}.",
                new Dictionary<string, object?> { ["indirect"] = Money.R4(total) }));
        }, ct);
        var billed = await RunStage("ENCOUNTER_OVERHEAD", _ =>
        {
            var values = ctx.Encounters.ToDictionary(e => e.Id, e => linesByEncounter.TryGetValue(e.Id, out var lines)
                ? lines.Sum(l => l.Quantity * (ctx.Procedures.GetValueOrDefault(l.ProcedureId)?.Price ?? 0m)) : 0m);
            var total = values.Values.Sum(v => OverheadOf(ctx, v));
            return (values, new StepResult(0, 0m, 0m, "SUCCESS", $"Encounter level overheads come to {Fmt.Sar(total)}.",
                new Dictionary<string, object?> { ["overhead"] = Money.R4(total) }));
        }, ct);
        await sink.StatusAsync("CALCULATED", ct);

        var (cost, revenue, totalDirect, totalIndirect, totalOverhead) = await RunStage("AGGREGATE",
            rows => Aggregate(ctx, linesByEncounter, direct, indirect, billed, rows), ct);

        var metrics = new Dictionary<string, object?>
        {
            ["costingMethod"] = Method, ["costingVersion"] = ctx.CostingVersionCode, ["patientCost"] = cost, ["revenue"] = revenue,
            ["direct"] = totalDirect, ["indirect"] = totalIndirect, ["overhead"] = totalOverhead,
            ["operatingMargin"] = Money.R4(revenue - cost), ["encounters"] = ctx.Encounters.Count, ["services"] = ctx.Lines.Count, ["qualityIssues"] = issues,
        };
        await sink.StatusAsync("RECONCILED", ct);
        return new PipelineOutcome("RECONCILED", 0m, cost, revenue, 0m, 0m, true, ctx.Encounters.Count, 0, issues, false, metrics);
    }

    private static (int, StepResult) Validate(CostingModelContext ctx, List<Guid> billedProcedures, StageOutput output)
    {
        void Add(string code, string entityType, string label, string message, string? value)
        {
            var def = QualityChecks.Get(code);
            output.Issues.Add(new DataQualityIssue
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, CheckCode = code, CheckName = def.Name, Category = def.Category, Severity = def.Severity,
                EntityType = entityType, EntityLabel = label, Message = message, Value = value, DetectedAt = ctx.CalculatedAt, Status = "OPEN",
            });
        }
        var withLines = ctx.Lines.Select(l => l.EncounterId).ToHashSet();
        var empty = ctx.Encounters.Count(e => !withLines.Contains(e.Id));
        if (empty > 0) Add("DQ-ENC-NO-SERVICE", "Encounter", $"{ctx.PeriodCode} encounters", $"{empty} encounter(s) recorded no services at all.", empty.ToString());
        var procedures = billedProcedures.Select(ctx.Procedures.GetValueOrDefault).OfType<ModelProcedure>().ToList();
        var noPct = procedures.Count(p => Pct(p, ctx.DirectItems, ctx.DirectTypes) is null);
        if (noPct > 0) Add("DQ-MODEL-NO-MARGIN", "Procedure", $"{ctx.PeriodCode} procedures",
            $"{noPct} billed procedure(s) have no Margin Cost % under {ctx.CostingVersionCode}, so their direct cost is SAR 0.", noPct.ToString());
        var noPrice = procedures.Count(p => p.Price == 0m);
        if (noPrice > 0) Add("DQ-MODEL-NO-PRICE", "Procedure", $"{ctx.PeriodCode} procedures",
            $"{noPrice} billed procedure(s) have no contracted price effective in {ctx.PeriodCode}, so they cost SAR 0.", noPrice.ToString());
        return (output.Issues.Count, new StepResult(output.Issues.Count, 0m, 0m, output.Issues.Count > 0 ? "WARNING" : "SUCCESS",
            $"{output.Issues.Count} data-quality warning(s) for {ctx.Encounters.Count} encounters and {ctx.Lines.Count} service lines."));
    }

    private static ((decimal Cost, decimal Revenue, decimal Direct, decimal Indirect, decimal Overhead), StepResult) Aggregate(CostingModelContext ctx,
        Dictionary<Guid, List<ModelLine>> linesByEncounter, Dictionary<Guid, decimal> directUnits, Dictionary<Guid, decimal> indirectUnits,
        Dictionary<Guid, decimal> billed, StageOutput output)
    {
        var revenueByEncounter = ctx.Revenue.GroupBy(r => r.EncounterId).ToDictionary(g => g.Key, g => g.Sum(r => r.Net));
        var netByLine = ctx.Revenue.GroupBy(r => (r.EncounterId, Ref: LineRef(r.SourceRef))).ToDictionary(g => g.Key, g => g.Sum(r => r.Net));
        var dimensions = new Dictionary<(string Dimension, string Key), Totals>();
        Totals At(string dimension, string key) => dimensions.TryGetValue((dimension, key), out var t) ? t : dimensions[(dimension, key)] = new Totals();
        void Add(string dimension, string key, Guid encounter, decimal rev, decimal d, decimal i, decimal o)
        {
            var t = At(dimension, key);
            t.Encounters.Add(encounter);
            t.Revenue += rev; t.Direct += d; t.Indirect += i; t.Overhead += o;
        }

        var procedureTotals = new Dictionary<Guid, (decimal Volume, decimal Direct, decimal Indirect, decimal Overhead, decimal Revenue)>();
        foreach (var e in ctx.Encounters)
        {
            var lines = linesByEncounter.GetValueOrDefault(e.Id) ?? [];
            var value = billed.GetValueOrDefault(e.Id);
            var overhead = OverheadOf(ctx, value);
            decimal d = 0m, i = 0m, assignedOverhead = 0m;
            foreach (var line in lines)
            {
                var lineDirect = line.Quantity * directUnits.GetValueOrDefault(line.ProcedureId);
                var lineIndirect = line.Quantity * indirectUnits.GetValueOrDefault(line.ProcedureId);
                var lineValue = line.Quantity * (ctx.Procedures.GetValueOrDefault(line.ProcedureId)?.Price ?? 0m);
                var lineOverhead = value > 0m ? overhead * (lineValue / value) : 0m;
                netByLine.Remove((line.EncounterId, LineRef(line.SourceRef)), out var net);
                d += lineDirect; i += lineIndirect; assignedOverhead += lineOverhead;
                var resource = line.ProviderId is { } doctor && ctx.DoctorResource.GetValueOrDefault(doctor) is { } r ? r.ToString() : "";
                Add("resource", resource, e.Id, net, lineDirect, lineIndirect, lineOverhead);
                Add("costCentre", line.CostCenterId.ToString(), e.Id, net, lineDirect, lineIndirect, lineOverhead);
                var p = procedureTotals.GetValueOrDefault(line.ProcedureId);
                procedureTotals[line.ProcedureId] = (p.Volume + line.Quantity, p.Direct + lineDirect, p.Indirect + lineIndirect, p.Overhead + lineOverhead, p.Revenue + net);
            }
            var unassigned = overhead - assignedOverhead;
            if (lines.Count == 0 || unassigned != 0m)
            {
                Add("resource", "", e.Id, 0m, 0m, 0m, unassigned);
                Add("costCentre", "", e.Id, 0m, 0m, 0m, unassigned);
            }

            var revenue = revenueByEncounter.GetValueOrDefault(e.Id);
            var (direct, indirect) = (Money.R4(d), Money.R4(i));
            var total = Money.R4(direct + indirect + overhead);
            output.EncounterCosts.Add(new EncounterCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, EncounterId = e.Id, DirectCost = direct, IndirectCost = indirect, OverheadCost = overhead,
                TotalCost = total, Revenue = Money.R4(revenue), ContributionMargin = Money.R4(revenue - direct - indirect),
                OperatingMargin = Money.R4(revenue - total), MarginPct = revenue > 0m ? Money.R6((revenue - total) / revenue * 100m) : 0m,
                CostPerDay = Money.R4(total / Math.Max(e.LengthOfStay, 1)), ServiceCount = lines.Count,
            });
            foreach (var (dimension, key) in new[]
                     {
                         ("hospital", ctx.HospitalId.ToString()), ("department", e.DepartmentId.ToString()), ("serviceLine", e.ServiceLineId?.ToString() ?? ""),
                         ("physician", e.ProviderId?.ToString() ?? ""), ("payer", e.PayerId.ToString()), ("package", e.PackageId?.ToString() ?? ""),
                         ("drg", e.DrgId?.ToString() ?? ""), ("encounterType", e.EncType), ("segment", e.Segment),
                     })
                Add(dimension, key, e.Id, revenue, direct, indirect, overhead);
        }
        foreach (var ((encounter, _), net) in netByLine)
        {
            Add("resource", "", encounter, net, 0m, 0m, 0m);
            Add("costCentre", "", encounter, net, 0m, 0m, 0m);
        }
        foreach (var listed in ctx.ListedResources.Keys) At("resource", listed.ToString());

        var labels = new Dictionary<string, Dictionary<string, string>>(ctx.Labels) { ["resource"] = ctx.ListedResources.ToDictionary(x => x.Key.ToString(), x => x.Value) };
        foreach (var ((dimension, key), t) in dimensions)
        {
            var (direct, indirect, overhead, revenue) = (Money.R4(t.Direct), Money.R4(t.Indirect), Money.R4(t.Overhead), Money.R4(t.Revenue));
            var total = Money.R4(direct + indirect + overhead);
            var label = key.Length == 0 ? "Not assigned"
                : dimension == "hospital" ? ctx.HospitalName
                : dimension is "encounterType" or "segment" ? key
                : labels.GetValueOrDefault(dimension)?.GetValueOrDefault(key) ?? key;
            output.DimensionCosts.Add(new RunDimensionCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, Dimension = dimension, Key = key, Label = label, Encounters = t.Encounters.Count, Revenue = revenue,
                DirectCost = direct, IndirectCost = indirect, OverheadCost = overhead, TotalCost = total, ContributionMargin = Money.R4(revenue - direct - indirect),
                OperatingMargin = Money.R4(revenue - total), MarginPct = revenue != 0m ? Money.R6((revenue - total) / revenue * 100m) : 0m,
            });
        }

        foreach (var p in ctx.Procedures.Values)
        {
            var (unitDirect, unitIndirect) = (directUnits.GetValueOrDefault(p.Id), indirectUnits.GetValueOrDefault(p.Id));
            output.ProcedureCosts.Add(new ProcedureCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, ProcedureId = p.Id, CostType = "STANDARD", Volume = 1m, DirectCost = unitDirect, IndirectCost = unitIndirect,
                TotalCost = Money.R4(unitDirect + unitIndirect), UnitCost = unitDirect,
            });
            if (!procedureTotals.TryGetValue(p.Id, out var a)) continue;
            var (direct, indirect, overhead, revenue) = (Money.R4(a.Direct), Money.R4(a.Indirect), Money.R4(a.Overhead), Money.R4(a.Revenue));
            var total = Money.R4(direct + indirect + overhead);
            output.ProcedureCosts.Add(new ProcedureCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, ProcedureId = p.Id, CostType = "ACTUAL", Volume = Money.R6(a.Volume), DirectCost = direct, IndirectCost = indirect,
                OverheadCost = overhead, TotalCost = total, UnitCost = a.Volume > 0m ? Money.R6(total / a.Volume) : 0m, Revenue = revenue,
                ContributionMargin = Money.R4(revenue - direct - indirect), OperatingMargin = Money.R4(revenue - total),
            });
        }

        var costs = output.EncounterCosts.ToDictionary(c => c.EncounterId);
        foreach (var g in ctx.Encounters.Where(e => e.PackageId is not null).GroupBy(e => e.PackageId!.Value))
        {
            var rows = g.Select(e => costs[e.Id]).ToList();
            var (actual, revenue) = (rows.Sum(r => r.TotalCost), rows.Sum(r => r.Revenue));
            output.PackageCosts.Add(new PackageCost
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, PackageId = g.Key, CaseCount = rows.Count, ActualCost = actual, StandardCost = actual, Revenue = revenue,
                ContributionMargin = Money.R4(revenue - rows.Sum(r => r.DirectCost + r.IndirectCost)), OperatingMargin = Money.R4(revenue - actual),
                MarginPct = revenue > 0m ? Money.R6((revenue - actual) / revenue * 100m) : 0m,
            });
        }
        foreach (var g in ctx.Encounters.Where(e => e.DrgId is not null).GroupBy(e => e.DrgId!.Value))
        {
            var values = g.Select(e => costs[e.Id].TotalCost).ToArray();
            var (avg, sd, p25, p75) = (Money.Mean(values), Money.StdDev(values), Money.Percentile(values, 25m), Money.Percentile(values, 75m));
            var (avgLos, avgRevenue) = (Money.Mean(g.Select(e => (decimal)e.LengthOfStay).ToArray()), Money.Mean(g.Select(e => costs[e.Id].Revenue).ToArray()));
            output.DrgSummaries.Add(new DrgCostSummary
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, DrgId = g.Key, CaseCount = values.Length, AvgCost = Money.R4(avg), MedianCost = Money.R4(Money.Percentile(values, 50m)),
                P25Cost = Money.R4(p25), P75Cost = Money.R4(p75), P90Cost = Money.R4(Money.Percentile(values, 90m)), MinCost = Money.R4(values.Min()), MaxCost = Money.R4(values.Max()),
                StdDevCost = Money.R4(sd), AvgLOS = Money.R6(avgLos), CostPerDay = Money.R4(avg / Math.Max(avgLos, 1m)), AvgReimbursement = Money.R4(avgRevenue),
                AvgMargin = Money.R4(avgRevenue - avg), MarginPct = avgRevenue > 0m ? Money.R6((avgRevenue - avg) / avgRevenue * 100m) : 0m,
                VariationCoeff = avg > 0m ? Money.R6(sd / avg) : 0m, OutlierCount = values.Count(c => c > p75 + 1.5m * (p75 - p25)),
            });
        }

        var totals = (Cost: output.EncounterCosts.Sum(c => c.TotalCost), Revenue: output.EncounterCosts.Sum(c => c.Revenue),
            Direct: output.EncounterCosts.Sum(c => c.DirectCost), Indirect: output.EncounterCosts.Sum(c => c.IndirectCost), Overhead: output.EncounterCosts.Sum(c => c.OverheadCost));
        return (totals, new StepResult(output.RowCount, totals.Cost, totals.Cost, "SUCCESS",
            $"{output.EncounterCosts.Count} encounters costed at {Fmt.Sar(totals.Cost)} against {Fmt.Sar(totals.Revenue)} of revenue."));
    }
}
