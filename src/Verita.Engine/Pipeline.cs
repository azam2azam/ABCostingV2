using Verita.Domain;
using Verita.Engine.Stages;

namespace Verita.Engine;

/// <summary>
/// Where a stage's rows and RunStep go. The engine never touches I/O itself (ARCH-1): the
/// infrastructure layer implements this against SQL Server with SqlBulkCopy; tests implement it
/// in memory.
/// </summary>
public interface IStageSink
{
    /// <summary>Called before a stage runs, so progress is visible while it works (ARCH-10).</summary>
    Task StageStartedAsync(Catalogue.RunStepDef step, CancellationToken ct);
    /// <summary>Persist the stage's rows and its RunStep. Re-executing a stage replaces only its own rows (ARCH-9).</summary>
    Task StageCompletedAsync(Catalogue.RunStepDef step, StepResult result, StageOutput rows, TimeSpan duration, CancellationToken ct);
    Task StageFailedAsync(Catalogue.RunStepDef step, string message, TimeSpan duration, CancellationToken ct);
    Task StatusAsync(string status, CancellationToken ct);
}

public sealed record PipelineOptions(bool Force = false);

public sealed record PipelineOutcome(
    string Status, decimal GlTotal, decimal PatientCost, decimal Revenue, decimal UnusedCapacity, decimal Unexplained,
    bool WithinMateriality, int Encounters, int Transactions, int QualityIssues, bool HasErrorIssues,
    IReadOnlyDictionary<string, object?> Metrics);

/// <summary>
/// The eleven stages in strict order (FR-2.2). Each stage reads only from the stages before it,
/// hands its rows to the sink and records a RunStep with money in and out (FR-2.4). The
/// conservation invariant is asserted at the end of the money-moving stages (FR-2.3, AC-3).
/// </summary>
public sealed class Pipeline(IStageSink sink)
{
    private static Catalogue.RunStepDef Step(string code) => Catalogue.RunSteps.First(s => s.Code == code);

    private async Task<T> RunStage<T>(string code, Func<StageOutput, (T Value, StepResult Step)> body, CancellationToken ct)
    {
        var def = Step(code);
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

    public async Task<PipelineOutcome> ExecuteAsync(RunContext ctx, IEnumerable<TxnRow> transactions, PipelineOptions options, CancellationToken ct = default)
    {
        await sink.StatusAsync("VALIDATING", ct);

        // The single pass over measured consumption (FR-2.10). It moves no money; the gate reads
        // its facts and every later stage reads its aggregates.
        var usage = UsageIndex.Build(ctx, transactions);

        var gate = await RunStage("VALIDATE", rows =>
        {
            var g = Quality.Validate(ctx, usage, rows);
            // A blocking issue stops the run before any money moves (FR-9.2); the stage itself
            // is recorded as a warning so the reason sits on the run, not in an exception.
            return (g, g.Step with { Status = g.Blocking > 0 ? "WARNING" : g.Step.Status });
        }, ct);
        if (gate.Blocking > 0)
            throw new RunFailedException($"{gate.Blocking} blocking data-quality issue(s) must be resolved before this period can be costed.", "VALIDATE");
        if (gate.Errors > 0 && !options.Force)
            throw new RunFailedException($"{gate.Errors} data-quality error(s) found. Resolve them, or re-run with force knowing the run cannot then be published (FR-9.8).", "VALIDATE");

        await sink.StatusAsync("VALIDATED", ct);
        await sink.StatusAsync("CALCULATING", ct);

        await RunStage("RESOURCE_RATES", _ =>
        {
            var resolved = ctx.Resources.Values.Count(r => r.RateId is not null);
            var missing = ctx.Resources.Count - resolved;
            return (0, new StepResult(0, 0m, 0m, missing > 0 ? "WARNING" : "SUCCESS",
                $"{resolved} of {ctx.Resources.Count} resources have exactly one rate effective at {Fmt.Date(ctx.AsOf)} under {ctx.CostingVersionCode}.",
                new Dictionary<string, object?> { ["resolved"] = resolved, ["missing"] = missing, ["asOf"] = Fmt.Date(ctx.AsOf) }));
        }, ct);

        var pools = await RunStage("POOL_BUILD", rows => Pools.Build(ctx, rows), ct);

        var capacityIssues = 0;
        var capacity = await RunStage("CAPACITY", rows =>
        {
            var c = Capacity.Compute(ctx, usage, pools, rows);
            capacityIssues = Quality.ValidateCapacity(ctx, rows.UnitCosts, rows);
            return c;
        }, ct);

        var support = await RunStage("ALLOCATE_SUPPORT", rows => Allocate.Support(ctx, pools, rows), ct);
        var alloc = await RunStage("ALLOCATE_ACTIVITY", rows => Allocate.ToActivities(ctx, usage, support, rows), ct);
        var standard = await RunStage("COST_STANDARD", rows => Costing.Standard(ctx, usage, alloc, rows), ct);

        var consumptionIssues = 0;
        var actual = await RunStage("COST_ACTUAL", rows =>
        {
            var a = Costing.Actual(ctx, usage, alloc, standard, capacity, rows);
            consumptionIssues = Quality.ValidateConsumption(ctx, a.Result.EncounterTotals.ToDictionary(kv => kv.Key, kv => kv.Value.TotalCost), rows);
            return a;
        }, ct);

        await RunStage("AGGREGATE", rows => (0, Aggregate.Run(ctx, actual, standard, rows)), ct);
        await RunStage("VARIANCE", rows => (0, VarianceStage.Run(ctx, usage, standard, actual, rows)), ct);

        await sink.StatusAsync("CALCULATED", ct);

        var recon = await RunStage("RECONCILE", rows =>
        {
            var o = Reconcile.Run(ctx, pools, capacity, alloc, actual, rows);
            // An unexplained difference beyond materiality is a finding about this run, not a
            // crash: the run still reaches RECONCILED and the publish gate refuses it (FR-7.2).
            return (o, o.Step with { Status = o.WithinMateriality ? o.Step.Status : "WARNING" });
        }, ct);

        var issues = gate.Step.RowsWritten + capacityIssues + consumptionIssues;
        var metrics = new Dictionary<string, object?>
        {
            ["glTotal"] = pools.GlTotal, ["patientCost"] = actual.PatientCostTotal, ["revenue"] = actual.RevenueTotal,
            ["operatingMargin"] = Money.R4(actual.RevenueTotal - actual.PatientCostTotal), ["unusedCapacity"] = capacity.TotalUnused,
            ["unexplained"] = recon.Unexplained, ["withinMateriality"] = recon.WithinMateriality,
            ["encounters"] = actual.EncounterTotals.Count, ["transactions"] = usage.TransactionCount,
            ["qualityIssues"] = issues, ["costingVersion"] = ctx.CostingVersionCode,
            ["reciprocalIterations"] = support.ReciprocalIterations, ["reciprocalResidual"] = support.ReciprocalResidual,
            ["attributionPct"] = pools.GlTotal > 0m ? Money.R4(actual.PatientCostTotal / pools.GlTotal * 100m) : 0m,
        };

        await sink.StatusAsync("RECONCILED", ct);
        return new PipelineOutcome("RECONCILED", pools.GlTotal, actual.PatientCostTotal, actual.RevenueTotal, capacity.TotalUnused,
            recon.Unexplained, recon.WithinMateriality, actual.EncounterTotals.Count, usage.TransactionCount, issues, gate.Errors > 0, metrics);
    }
}
