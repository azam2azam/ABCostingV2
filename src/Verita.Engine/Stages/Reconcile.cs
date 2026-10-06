using System.Text.Json;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

/// <summary>
/// Stage 11 — reconcile the model to the ledger (FR-7). Never silently force reconciliation:
/// every currency unit that did not reach a patient sits in a named bucket with an owner, and
/// whatever is left is reported as unexplained — not absorbed, not rounded away, not netted.
/// </summary>
public static class Reconcile
{
    /// <summary>FR-7.2: materiality is the greater of one currency unit and one basis point.</summary>
    public static decimal Materiality(decimal glTotal) => Math.Max(1m, Money.R4(glTotal * 0.0001m));

    /// <summary>Drift below this is distribution rounding; anything above is unexplained (FR-7.4).</summary>
    public const decimal RoundingCeiling = 1m;

    public sealed record Outcome(StepResult Step, decimal Unexplained, bool WithinMateriality);

    public static Outcome Run(RunContext ctx, PoolBuildResult pools, CapacityResult capacity, ActivityAllocationResult alloc, ActualCostResult actual, StageOutput output)
    {
        var gl = pools.GlTotal;
        var patient = actual.PatientCostTotal;
        var residuals = new List<ResidualEntry>(capacity.Residuals);

        if (pools.UnmappedTotal != 0m)
            residuals.Add(new("UNMAPPED_GL", Catalogue.ResidualLabel["UNMAPPED_GL"], pools.UnmappedTotal,
                "Postings on accounts with no effective cost-pool mapping. A data-quality defect, not a costing decision: until the mapping is added this spend is invisible to every cost object below it, and DQ-GL-UNMAPPED blocks publication.",
                pools.UnmappedDetail));
        if (alloc.NoDriverVolume != 0m)
            residuals.Add(new("NO_DRIVER_VOLUME", Catalogue.ResidualLabel["NO_DRIVER_VOLUME"], alloc.NoDriverVolume,
                "Cost centre balances whose allocation driver measured no volume in the period. Dividing by a substitute denominator would have moved the money somewhere arbitrary, so it is held here.",
                alloc.NoDriverDetail));
        if (alloc.OrphanedOverhead != 0m)
            residuals.Add(new("NON_PATIENT_ACTIVITY", "Overhead on activities with no patient consumer", alloc.OrphanedOverhead,
                "Overhead allocated to activities no patient consumed in the period — teaching, research, standby cover and maintenance. A real cost of running the hospital, but not a cost of any particular patient.",
                alloc.OrphanDetail));

        var residualTotal = residuals.Sum(r => r.Amount);
        var gap = Money.R4(gl - patient - residualTotal);
        var materiality = Materiality(gl);

        // FR-7.4: rounding is reported apart from the unexplained, and only true minor-unit drift
        // qualifies. Anything larger stays visible as unexplained even when within materiality.
        decimal unexplained = gap;
        if (gap != 0m && Math.Abs(gap) < RoundingCeiling)
        {
            residuals.Add(new("ROUNDING", Catalogue.ResidualLabel["ROUNDING"], gap,
                "Accumulated drift from storing distributed amounts at four decimal places.", null));
            residualTotal += gap;
            unexplained = 0m;
        }
        var within = Math.Abs(unexplained) <= materiality;
        var status = within ? (unexplained == 0m ? "PASS" : "WARN") : "FAIL";

        foreach (var r in residuals)
            output.Residuals.Add(new ResidualBucket
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, Bucket = r.Bucket, Label = r.Label, Amount = Money.R4(r.Amount),
                Explanation = r.Explanation, DetailJson = r.Detail is null ? null : JsonSerializer.Serialize(r.Detail),
            });

        var attribution = gl > 0m ? Money.R6(patient / gl * 100m) : 0m;
        var narrative = Narrative(gl, patient, residuals, unexplained, materiality, within, attribution);
        var rec = new Reconciliation
        {
            Id = IdGen.Sequential(), RunId = ctx.RunId, GlTotal = gl, PatientCost = patient, ResidualTotal = Money.R4(residualTotal),
            Unexplained = unexplained, Materiality = materiality, AttributionPct = attribution, Status = status, Narrative = narrative,
        };
        output.Reconciliations.Add(rec);

        // FR-7.6: ledger, each named residual, patient cost, then unexplained — with a running balance.
        var seq = 1;
        var running = gl;
        void Line(string label, string type, string? bucket, decimal amount, string explanation)
        {
            if (type != "SOURCE") running += amount;
            output.ReconciliationLines.Add(new ReconciliationLine
            {
                Id = IdGen.Sequential(), ReconciliationId = rec.Id, Sequence = seq++, Label = label, LineType = type, Bucket = bucket,
                Amount = Money.R4(amount), RunningBalance = Money.R4(running), Explanation = explanation,
            });
        }
        Line($"General ledger — {ctx.HospitalName}, {ctx.PeriodCode}", "SOURCE", null, gl,
            "Total operating expenditure posted to the period: the only source of monetary truth in the model.");
        // FR-7.10: each residual is its own line; a positive is never netted against a negative.
        foreach (var r in residuals.OrderByDescending(r => r.Amount))
            Line(r.Label, "RESIDUAL", r.Bucket, -r.Amount, $"{r.Explanation} Owner: {Catalogue.ResidualOwner[r.Bucket]}.");
        Line("Cost attributed to patient encounters", "RESULT", null, -patient,
            $"Sum of {actual.EncounterTotals.Count:N0} encounter cost records produced by this run.");
        Line("Unexplained difference", "UNEXPLAINED", null, -unexplained,
            within
                ? $"Within the {Fmt.Sar(materiality, 2)} materiality threshold."
                : "Ledger cost this run could neither attribute to a patient nor place in a named residual. The run cannot be published until it is explained.");

        return new Outcome(new StepResult(output.Residuals.Count + output.ReconciliationLines.Count + 1, gl, Money.R4(patient + residualTotal + unexplained),
            within ? "SUCCESS" : "FAILED", narrative,
            new Dictionary<string, object?>
            {
                ["glTotal"] = gl, ["patientCost"] = patient, ["residualTotal"] = Money.R4(residualTotal), ["unexplained"] = unexplained,
                ["materiality"] = materiality, ["attributionPct"] = attribution,
            }), unexplained, within);
    }

    private static string Narrative(decimal gl, decimal patient, List<ResidualEntry> residuals, decimal unexplained, decimal materiality, bool within, decimal attribution)
    {
        var top = residuals.OrderByDescending(r => Math.Abs(r.Amount)).Take(3).Select(r => $"{r.Label.ToLowerInvariant()} {Fmt.Sar(r.Amount)}").ToList();
        var head = $"{Fmt.Sar(gl)} of ledger cost resolved to {Fmt.Sar(patient)} attributed to patients ({Fmt.P(attribution)}%), " +
                   $"with {Fmt.Sar(residuals.Sum(r => r.Amount))} in named residuals" + (top.Count > 0 ? $": {string.Join(", ", top)}." : ".");
        return within
            ? $"{head} Unexplained difference {Fmt.Sar(Math.Abs(unexplained), 2)}, within the {Fmt.Sar(materiality, 2)} materiality threshold."
            : $"{head} Unexplained difference {Fmt.Sar(unexplained, 2)} exceeds the {Fmt.Sar(materiality, 2)} materiality threshold — this run cannot be published until it is accounted for.";
    }
}
