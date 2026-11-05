using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

public sealed record RunExecution(Guid RunId, string Code, string Status, int DurationMs, IReadOnlyDictionary<string, object?> Metrics, string? Error);

/// <summary>
/// The run lifecycle (FR-2.1, FR-2.7, FR-2.8). A CalculationRun is the only way costs are
/// produced; illegal transitions are refused here, in the service layer, whatever the caller.
/// </summary>
public sealed class RunService(IDbContextFactory<VeritaDbContext> factory, VeritaConnection connection, IAuditWriter audit, ILogger<RunService> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The costing version a new run for this period would use: effective at the period end, a published one first, else a draft.</summary>
    public static async Task<CostingVersion?> AppliedVersionAsync(VeritaDbContext db, Guid hospitalId, Period period, CancellationToken ct)
    {
        var effective = (await db.CostingVersions.Where(v => v.HospitalId == hospitalId && (v.Status == "PUBLISHED" || v.Status == "DRAFT")).ToListAsync(ct))
            .Where(v => v.IsEffective(period.EndDate)).OrderByDescending(v => v.EffectiveFrom).ToList();
        return effective.FirstOrDefault(v => v.Status == "PUBLISHED") ?? effective.FirstOrDefault();
    }

    /// <summary>The run a period already has under a costing version; there is only ever one. Simulations do not count.</summary>
    public static Task<string?> ExistingRunCodeAsync(VeritaDbContext db, Guid hospitalId, Guid periodId, Guid versionId, CancellationToken ct) =>
        db.CalculationRuns.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId && r.PeriodId == periodId && r.CostingVersionId == versionId && r.RunType != "SIMULATION")
            .Select(r => r.Code).FirstOrDefaultAsync(ct);

    /// <summary>Create a run under the costing version that applies at the period end.</summary>
    public async Task<CalculationRun> CreateAsync(Guid hospitalId, Guid periodId, string triggeredBy, string? name = null,
        string runType = "ACTUAL", Guid? id = null, string? code = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var period = await db.Periods.FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppError.NotFound("Period");
        var hospital = await db.Hospitals.FirstOrDefaultAsync(h => h.Id == hospitalId, ct) ?? throw AppError.NotFound("Hospital");
        if (period.HospitalId is { } owner && owner != hospitalId)
            throw AppError.Invalid($"Period {period.Code} belongs to another hospital.");
        var version = await AppliedVersionAsync(db, hospitalId, period, ct)
            ?? throw AppError.Conflict("NO_COSTING_VERSION", $"No published or draft costing version is effective for {period.Code}.");
        if (runType != "SIMULATION" && await ExistingRunCodeAsync(db, hospitalId, periodId, version.Id, ct) is { } taken)
            throw AppError.Conflict("RUN_EXISTS", $"{taken} already exists for {period.Code} under costing model {version.Code}. Re-run it, or delete it first.");

        if (code is null)
        {
            var existing = await db.CalculationRuns.CountAsync(r => r.HospitalId == hospitalId && r.PeriodId == periodId && r.RunType != "SIMULATION", ct);
            code = runType == "SIMULATION"
                ? $"SIM-{hospital.Code}-{period.Code}-{DateTime.UtcNow:HHmmssfff}"
                : $"RUN-{hospital.Code}-{period.Code.Replace(' ', '-')}{(existing > 0 ? $"-R{existing + 1}" : "")}";
        }

        var run = new CalculationRun
        {
            Id = id ?? IdGen.Sequential(), Code = code, Name = name ?? RunName(hospital.Name, period),
            HospitalId = hospitalId, PeriodId = periodId, CostingVersionId = version.Id, Status = "DRAFT", RunType = runType,
            TriggeredBy = triggeredBy, CreatedAt = DateTime.UtcNow,
        };
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private static string RunName(string hospitalName, Period period) =>
        period.HospitalId is null
            ? $"{period.Code} costing — {hospitalName}"
            : $"{period.StartDate:yyyy-MM-dd} – {period.EndDate:yyyy-MM-dd} costing — {hospitalName}";

    /// <summary>Add a period to a hospital's calendar. It may not overlap one the hospital already has.</summary>
    public async Task<string> AddPeriodAsync(Guid hospitalId, string? code, DateOnly start, DateOnly end, Principal actor, CancellationToken ct = default)
    {
        if (end < start) throw AppError.Invalid("The period ends before it starts.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var owned = await db.Periods.AsNoTracking().Where(p => p.HospitalId == hospitalId).ToListAsync(ct);
        var name = string.IsNullOrWhiteSpace(code) ? start.ToString("yyyy MMM", System.Globalization.CultureInfo.InvariantCulture) : code.Trim();
        if (owned.Any(p => string.Equals(p.Code, name, StringComparison.OrdinalIgnoreCase)))
            throw AppError.Conflict("PERIOD_EXISTS", $"This hospital already has a period named {name}.");
        if (owned.FirstOrDefault(p => p.StartDate <= end && p.EndDate >= start) is { } clash)
            throw AppError.Conflict("PERIOD_OVERLAP", $"These dates overlap {clash.Code} ({clash.StartDate:yyyy-MM-dd} – {clash.EndDate:yyyy-MM-dd}).");
        var period = new Period { Id = IdGen.Sequential(), Code = name, StartDate = start, EndDate = end, Days = end.DayNumber - start.DayNumber + 1, Status = "OPEN", HospitalId = hospitalId };
        db.Periods.Add(period);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "CREATE", "Period", period.Id.ToString(), name, ct: ct);
        return name;
    }

    /// <summary>Remove a hospital's own period, only while nothing was calculated or posted against it.</summary>
    public async Task DeletePeriodAsync(Guid periodId, Principal actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var period = await db.Periods.FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppError.NotFound("Period");
        if (period.HospitalId is not { } hospitalId)
            throw AppError.Conflict("PERIOD_SHARED", $"{period.Code} is on the shared calendar used by other hospitals and cannot be deleted.");
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == hospitalId, ct);
        if (!actor.HasHospital(hospital.Code)) throw AppError.OutOfScope($"Period {period.Code}");
        if (await db.CalculationRuns.AnyAsync(r => r.PeriodId == periodId, ct))
            throw AppError.Conflict("PERIOD_HAS_RUN", $"{period.Code} has a calculation run. Delete the run first.");
        if (await db.GLPostings.AnyAsync(g => g.PeriodId == periodId, ct) || await db.DriverVolumes.AnyAsync(d => d.PeriodId == periodId, ct))
            throw AppError.Conflict("PERIOD_HAS_DATA", $"{period.Code} has ledger postings or driver volumes and cannot be deleted.");
        var before = new { period.Code, period.StartDate, period.EndDate, hospital = hospital.Code };
        db.Periods.Remove(period);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "DELETE", "Period", periodId.ToString(), before.Code, before: before, ct: ct);
    }

    /// <summary>Remove a DRAFT or FAILED run. Same permission as New run. Reconciled or published runs stay.</summary>
    public async Task DeleteDraftAsync(Guid runId, Principal actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.CalculationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw AppError.RunNotFound(runId.ToString());
        var versionStatus = await db.CostingVersions.Where(v => v.Id == run.CostingVersionId).Select(v => v.Status).FirstAsync(ct);
        if (!Catalogue.IsRunDeletable(run.Status, versionStatus))
            throw AppError.Conflict("RUN_STATE", versionStatus == "PUBLISHED"
                ? $"{run.Code} was calculated on a published costing model and cannot be deleted."
                : $"{run.Code} is {run.Status} and cannot be deleted.");
        if (run.StartedAt is not null && run.FinishedAt is null)
            throw AppError.Conflict("RUN_IN_FLIGHT", $"{run.Code} is executing and cannot be deleted.");
        if (await db.Scenarios.AnyAsync(s => s.BaseRunId == run.Id, ct))
            throw AppError.Conflict("RUN_IN_USE", $"{run.Code} is the base of a simulation scenario.");

        var status = run.Status;
        await ClearResultsAsync(db, run.Id, ct);
        await db.ApprovalRequests.Where(a => a.EntityType == "CALCULATION_RUN" && a.EntityId == run.Id).ExecuteDeleteAsync(ct);
        var code = run.Code;
        var id = run.Id;
        db.CalculationRuns.Remove(run);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "DELETE", "CalculationRun", id.ToString(), code, before: new { status }, ct: ct);
    }

    /// <summary>Delete what a previous execution wrote, so a re-run is idempotent (ARCH-9).</summary>
    private static async Task ClearResultsAsync(VeritaDbContext db, Guid runId, CancellationToken ct)
    {
        await db.VarianceComponents.Where(c => db.Variances.Any(v => v.Id == c.VarianceId && v.RunId == runId)).ExecuteDeleteAsync(ct);
        await db.ReconciliationLines.Where(l => db.Reconciliations.Any(r => r.Id == l.ReconciliationId && r.RunId == runId)).ExecuteDeleteAsync(ct);
        await db.Variances.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.Reconciliations.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.ResidualBuckets.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.DataQualityIssues.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.CostComponents.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.DrgCostSummaries.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.PackageCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.RunDimensionCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.EncounterCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.ProcedureCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.ActivityCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.AllocationResults.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.CostCenterBalances.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.CostPoolBalances.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.ResourceUnitCosts.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
        await db.RunSteps.Where(x => x.RunId == runId).ExecuteDeleteAsync(ct);
    }

    public async Task<RunExecution> ExecuteAsync(Guid runId, bool force, string actor, ScenarioOverrides? overrides = null, CancellationToken ct = default, int distributionScale = 4)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.CalculationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw AppError.RunNotFound(runId.ToString());
        // FR-10.7: re-executing a published run is refused with its own code.
        if (Catalogue.IsRunImmutable(run.Status)) throw AppError.RunImmutable(run.Code);
        if (run.Status is not ("DRAFT" or "FAILED" or "RECONCILED" or "CALCULATED" or "VALIDATING"))
            throw AppError.Conflict("RUN_STATE", $"A run in {run.Status} cannot be executed; it must be DRAFT, FAILED or RECONCILED.");

        var costingModel = await CostingModelLoader.HasSetupAsync(db, run.HospitalId, run.CostingVersionId, ct);
        if (costingModel && overrides is not null)
            throw AppError.Conflict("SIMULATION_UNAVAILABLE", "Simulations are not available for a hospital costed by Direct and Indirect costing.");

        await ClearResultsAsync(db, runId, ct);
        run.CostingMethod = costingModel ? CostingModelPipeline.Method : "ENGINE";
        if (run.Status is not ("VALIDATING" or "VALIDATED" or "CALCULATING" or "CALCULATED"))
            run.Status = "DRAFT";
        run.StartedAt = DateTime.UtcNow;
        run.FinishedAt = null;
        run.ErrorMessage = null;
        run.Forced = false;
        run.ApprovedBy = null;
        await db.SaveChangesAsync(ct);

        var started = DateTime.UtcNow;
        try
        {
            PipelineOutcome outcome;
            var sink = new SqlStageSink(connection.Value, factory, runId);
            if (costingModel)
            {
                outcome = await new CostingModelPipeline(sink).ExecuteAsync(await new CostingModelLoader(db).LoadAsync(run, ct), ct);
            }
            else
            {
                var ctx = await new RunContextLoader(db).LoadAsync(runId, overrides, ct, distributionScale);
                var (start, end) = (ctx.PeriodStart.ToDateTime(TimeOnly.MinValue), ctx.PeriodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue));
                var txns = RunContextLoader.StreamTransactions(connection.Value, ctx.HospitalId, start, end);
                outcome = await new Pipeline(sink).ExecuteAsync(ctx, txns, new PipelineOptions(force), ct);
            }

            await db.Entry(run).ReloadAsync(ct);
            run.Status = "RECONCILED";
            run.FinishedAt = DateTime.UtcNow;
            run.DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            run.Forced = force && outcome.HasErrorIssues;
            run.MetricsJson = JsonSerializer.Serialize(outcome.Metrics, Json);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(null, "RUN", "CalculationRun", run.Id.ToString(), run.Code, metadata: new { actor, force, outcome.Status, run.DurationMs }, ct: ct);
            return new RunExecution(run.Id, run.Code, run.Status, run.DurationMs ?? 0, outcome.Metrics, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is RunFailedException) log.LogInformation("Run {Code} stopped: {Message}", run.Code, ex.Message);
            else log.LogWarning(ex, "Run {Code} failed", run.Code);
            await db.Entry(run).ReloadAsync(ct);
            run.Status = "FAILED";
            run.FinishedAt = DateTime.UtcNow;
            run.DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            run.ErrorMessage = ex is RunFailedException rf && rf.StepCode is not null ? $"[{rf.StepCode}] {ex.Message}" : ex.Message;
            await db.SaveChangesAsync(ct);
            return new RunExecution(run.Id, run.Code, run.Status, run.DurationMs ?? 0, new Dictionary<string, object?>(), run.ErrorMessage);
        }
    }

    /// <summary>
    /// Why a run may not publish, or an empty list. The gates are the BRD's: a forced run
    /// (FR-9.8), an open BLOCKING or ERROR data-quality issue (FR-9.2), and an unexplained
    /// difference beyond materiality (FR-7.2, AC-1).
    /// </summary>
    public async Task<List<string>> PublishBlockersAsync(VeritaDbContext db, CalculationRun run, CancellationToken ct)
    {
        var reasons = new List<string>();
        var versionStatus = await db.CostingVersions.Where(v => v.Id == run.CostingVersionId).Select(v => v.Status).FirstAsync(ct);
        if (versionStatus != "PUBLISHED") reasons.Add($"The run's costing model is {versionStatus}; publish the costing model first.");
        if (run.Forced) reasons.Add("The run was forced past data-quality errors (FR-9.8), so it can never be published.");
        var gating = await db.DataQualityIssues.Where(i => i.RunId == run.Id && (i.Severity == "BLOCKING" || i.Severity == "ERROR") && i.Status == "OPEN")
            .GroupBy(i => i.CheckCode).Select(g => new { Code = g.Key, N = g.Count() }).ToListAsync(ct);
        foreach (var g in gating) reasons.Add($"{g.N} open {g.Code} issue(s) block publication (FR-9.2).");
        // A costing-model run costs from contracted prices, so there is no ledger to reconcile to.
        if (run.CostingMethod == CostingModelPipeline.Method)
        {
            if (run.Status != "RECONCILED") reasons.Add("The run has not finished.");
            return reasons;
        }
        var rec = await db.Reconciliations.FirstOrDefaultAsync(r => r.RunId == run.Id, ct);
        if (rec is null) reasons.Add("The run has not reconciled.");
        else if (rec.Status == "FAIL") reasons.Add($"Unexplained difference SAR {rec.Unexplained:N2} exceeds materiality SAR {rec.Materiality:N2} (FR-7.2).");
        return reasons;
    }

    /// <summary>Move a run along FR-2.7. Only the legal transitions are accepted.</summary>
    public async Task<CalculationRun> TransitionAsync(Guid runId, string to, Principal actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.CalculationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw AppError.RunNotFound(runId.ToString());
        if (Catalogue.IsRunImmutable(run.Status)) throw AppError.RunImmutable(run.Code);
        if (!Catalogue.CanTransition(run.Status, to))
            throw AppError.Conflict("ILLEGAL_TRANSITION", $"A run cannot move from {run.Status} to {to}.");

        if (to == "PUBLISHED")
        {
            if (!actor.Can("run.publish")) throw AppError.Forbidden("run.publish");
            var blockers = await PublishBlockersAsync(db, run, ct);
            if (blockers.Count > 0) throw AppError.Conflict("PUBLISH_BLOCKED", "This run cannot be published.", new { reasons = blockers });
            run.PublishedAt = DateTime.UtcNow;
        }
        var before = run.Status;
        run.Status = to;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, to == "PUBLISHED" ? "PUBLISH" : "UPDATE", "CalculationRun", run.Id.ToString(), run.Code,
            new { status = before }, new { status = to }, ct: ct);
        return run;
    }
}

/// <summary>The SQL Server connection string, shared by the EF and Dapper paths.</summary>
public sealed record VeritaConnection(string Value);
