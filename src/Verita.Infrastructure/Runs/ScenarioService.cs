using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// What-if scenarios (FR-8). The scenario runs through the ordinary pipeline on a shadow run
/// carrying the levers as overrides; the shadow run is deleted once the comparison is written
/// (FR-8.2), and a scenario is never promoted into a costing version automatically (FR-8.3).
/// </summary>
public sealed class ScenarioService(IDbContextFactory<VeritaDbContext> factory, RunService runs, IAuditWriter audit)
{
    public async Task<Scenario> CreateAsync(Guid baseRunId, string code, string name, string description, IReadOnlyList<LeverInput> levers,
        string createdBy, Guid? id = null, CancellationToken ct = default)
    {
        if (levers.Count == 0) throw AppError.Invalid("A scenario needs at least one lever.");
        foreach (var l in levers)
        {
            if (!Catalogue.LeverTypes.Contains(l.LeverType)) throw AppError.Invalid($"Unknown lever type {l.LeverType}.", new { allowed = Catalogue.LeverTypes });
            if (!Catalogue.ChangeTypes.Contains(l.ChangeType)) throw AppError.Invalid($"Unknown change type {l.ChangeType}.", new { allowed = Catalogue.ChangeTypes });
            if (!Catalogue.LeverTargetTypes.Contains(l.TargetType)) throw AppError.Invalid($"Unknown target type {l.TargetType}.", new { allowed = Catalogue.LeverTargetTypes });
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.CalculationRuns.AnyAsync(r => r.Id == baseRunId, ct)) throw AppError.RunNotFound(baseRunId.ToString());
        var scenario = new Scenario
        {
            Id = id ?? IdGen.Sequential(), Code = code, Name = name, Description = description, BaseRunId = baseRunId,
            Status = "DRAFT", CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
        };
        db.Scenarios.Add(scenario);
        db.ScenarioLevers.AddRange(levers.Select(l => new ScenarioLever
        {
            Id = IdGen.Sequential(), ScenarioId = scenario.Id, LeverType = l.LeverType, TargetType = l.TargetType, TargetId = l.TargetId,
            TargetLabel = l.TargetLabel, ChangeType = l.ChangeType, Value = l.Value,
        }));
        await db.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<List<ScenarioResult>> RunAsync(Guid scenarioId, string actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var scenario = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == scenarioId, ct) ?? throw AppError.NotFound("Scenario");
        var baseRun = await db.CalculationRuns.FirstAsync(r => r.Id == scenario.BaseRunId, ct);
        if (baseRun.Status is not ("RECONCILED" or "REVIEWED" or "APPROVED" or "PUBLISHED"))
            throw AppError.Conflict("BASE_NOT_RECONCILED", "A scenario can only be built on a run that has reconciled.");

        var levers = await db.ScenarioLevers.Where(l => l.ScenarioId == scenarioId).ToListAsync(ct);
        var hid = baseRun.HospitalId;
        var resources = await db.Resources.Where(r => r.HospitalId == hid).Select(r => new SimResource(r.Id, r.ResourceType)).ToListAsync(ct);
        var activityIds = await db.Activities.Where(a => a.HospitalId == hid).Select(a => a.Id).ToListAsync(ct);
        var procedureIds = await db.Procedures.Where(p => p.HospitalId == hid).Select(p => p.Id).ToListAsync(ct);
        var payerIds = await db.Payers.Select(p => p.Id).ToListAsync(ct);
        var overrides = Simulation.BuildOverrides(
            levers.Select(l => new LeverInput(l.LeverType, l.TargetType, l.TargetId, l.TargetLabel, l.ChangeType, l.Value)).ToList(),
            resources, activityIds, procedureIds, payerIds);

        scenario.Status = "RUNNING";
        await db.ScenarioResults.Where(r => r.ScenarioId == scenarioId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        var shadow = await runs.CreateAsync(hid, baseRun.PeriodId, actor, $"Scenario shadow — {scenario.Name}", "SIMULATION", ct: ct);
        try
        {
            var exec = await runs.ExecuteAsync(shadow.Id, force: true, actor, overrides, ct);
            if (exec.Status != "RECONCILED") throw AppError.Conflict("SCENARIO_FAILED", $"The shadow run failed: {exec.Error}");

            var rows = Simulation.Compare(scenarioId, levers.Count, await SnapshotAsync(db, baseRun.Id, ct), await SnapshotAsync(db, shadow.Id, ct));
            db.ScenarioResults.AddRange(rows);
            scenario.Status = "COMPLETED";
            scenario.ExecutedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(null, "RUN", "Scenario", scenario.Id.ToString(), scenario.Name, metadata: new { actor, results = rows.Count }, ct: ct);
            return rows;
        }
        catch
        {
            scenario.Status = "FAILED";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            // FR-8.2: the shadow run is scaffolding; only the comparison survives.
            await db.CalculationRuns.Where(r => r.Id == shadow.Id).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static async Task<RunSnapshot> SnapshotAsync(VeritaDbContext db, Guid runId, CancellationToken ct)
    {
        var enc = await db.EncounterCosts.Where(e => e.RunId == runId).GroupBy(_ => 1).Select(g => new
        {
            Cost = g.Sum(e => e.TotalCost), Revenue = g.Sum(e => e.Revenue), Direct = g.Sum(e => e.DirectCost),
            Indirect = g.Sum(e => e.IndirectCost), Overhead = g.Sum(e => e.OverheadCost), N = g.Count(),
        }).FirstOrDefaultAsync(ct);
        var idle = await db.ResidualBuckets.Where(b => b.RunId == runId && b.Bucket == "UNUSED_CAPACITY").SumAsync(b => (decimal?)b.Amount, ct) ?? 0m;
        var procs = await (from pc in db.ProcedureCosts
                           join p in db.Procedures on pc.ProcedureId equals p.Id
                           where pc.RunId == runId && pc.CostType == "ACTUAL"
                           select new { pc.ProcedureId, p.Name, pc.TotalCost, pc.Revenue, pc.Volume, pc.UnitCost }).ToListAsync(ct);
        return new RunSnapshot(enc?.Cost ?? 0m, enc?.Revenue ?? 0m, enc?.Direct ?? 0m, enc?.Indirect ?? 0m, enc?.Overhead ?? 0m, enc?.N ?? 0, idle,
            procs.ToDictionary(p => p.ProcedureId, p => (p.Name, p.TotalCost, p.Revenue, p.Volume, p.UnitCost)));
    }
}
