using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// Persists each stage as it completes: its RunStep (the resume point and the progress the client
/// polls, ARCH-9/ARCH-10) and its result rows through SqlBulkCopy (ARCH-4). Rows are written with
/// constraint checking on, so the CostComponent arithmetic check guards every write.
/// </summary>
public sealed class SqlStageSink(string connectionString, IDbContextFactory<VeritaDbContext> factory, Guid runId) : IStageSink
{
    private readonly Dictionary<string, Guid> _stepIds = [];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task StageStartedAsync(Catalogue.RunStepDef step, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = new RunStep
        {
            Id = IdGen.Sequential(), RunId = runId, StepNo = step.No, StepCode = step.Code, StepName = step.Name,
            Status = "RUNNING", StartedAt = DateTime.UtcNow,
        };
        db.RunSteps.Add(row);
        await db.SaveChangesAsync(ct);
        _stepIds[step.Code] = row.Id;
    }

    public async Task StageCompletedAsync(Catalogue.RunStepDef step, StepResult result, StageOutput rows, TimeSpan duration, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var writer = new BulkWriter(db.Model);
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            // Parents before children, so foreign keys hold at write time.
            await writer.WriteAsync(conn, tx, rows.Issues, ct: ct);
            await writer.WriteAsync(conn, tx, rows.PoolBalances, ct: ct);
            await writer.WriteAsync(conn, tx, rows.UnitCosts, ct: ct);
            await writer.WriteAsync(conn, tx, rows.CcBalances, ct: ct);
            await writer.WriteAsync(conn, tx, rows.Allocations, ct: ct);
            await writer.WriteAsync(conn, tx, rows.ActivityCosts, ct: ct);
            await writer.WriteAsync(conn, tx, rows.ProcedureCosts, ct: ct);
            await writer.WriteAsync(conn, tx, rows.EncounterCosts, ct: ct);
            await writer.WriteAsync(conn, tx, rows.Components, ct: ct);
            await writer.WriteAsync(conn, tx, rows.PackageCosts, ct: ct);
            await writer.WriteAsync(conn, tx, rows.DrgSummaries, ct: ct);
            await writer.WriteAsync(conn, tx, rows.Variances, ct: ct);
            await writer.WriteAsync(conn, tx, rows.VarianceComponents, ct: ct);
            await writer.WriteAsync(conn, tx, rows.Residuals, ct: ct);
            await writer.WriteAsync(conn, tx, rows.Reconciliations, ct: ct);
            await writer.WriteAsync(conn, tx, rows.ReconciliationLines, ct: ct);
            await tx.CommitAsync(ct);
        }

        var stepRow = await db.RunSteps.FirstAsync(s => s.Id == _stepIds[step.Code], ct);
        stepRow.Status = result.Status;
        stepRow.FinishedAt = DateTime.UtcNow;
        stepRow.DurationMs = (int)duration.TotalMilliseconds;
        stepRow.RowsWritten = rows.RowCount;
        stepRow.MoneyIn = Money.R4(result.MoneyIn);
        stepRow.MoneyOut = Money.R4(result.MoneyOut);
        stepRow.Message = result.Message;
        stepRow.MetricsJson = result.Metrics is null ? null : JsonSerializer.Serialize(result.Metrics, Json);
        await db.SaveChangesAsync(ct);
    }

    public async Task StageFailedAsync(Catalogue.RunStepDef step, string message, TimeSpan duration, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (_stepIds.TryGetValue(step.Code, out var id) && await db.RunSteps.FirstOrDefaultAsync(s => s.Id == id, ct) is { } row)
        {
            row.Status = "FAILED";
            row.FinishedAt = DateTime.UtcNow;
            row.DurationMs = (int)duration.TotalMilliseconds;
            row.Message = message;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task StatusAsync(string status, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.CalculationRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status), ct);
    }
}
