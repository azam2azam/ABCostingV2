using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verita.Infrastructure.Data;

namespace Verita.Api.Infrastructure;

/// <summary>
/// Applies pending EF Core migrations when the API starts (IIS, Docker, Fly). The database's
/// __EFMigrationsHistory says what it already has — a database built from deploy/iis/sql/01-schema.sql
/// records every migration in that script — so only the newer ones run, each in its own transaction.
/// An application lock keeps two starting instances (an IIS overlapped recycle) from migrating at once.
/// The API's SQL login needs rights to change the schema (db_owner, or db_ddladmin + read/write).
/// </summary>
public static class DatabaseMigrator
{
    private const string LockName = "Verita.Migrations";

    public static async Task MigrateAsync(IServiceProvider services, ILogger log, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<VeritaDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0)
            {
                log.LogInformation("Database schema is up to date ({Count} migrations applied)", (await db.Database.GetAppliedMigrationsAsync(ct)).Count());
                return;
            }

            // Hold a session lock on the connection EF will use, so a second instance waits instead of racing.
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var conn = (SqlConnection)db.Database.GetDbConnection();
                await using (var lockCmd = conn.CreateCommand())
                {
                    lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 600000; SELECT @r;";
                    lockCmd.CommandTimeout = 660;
                    lockCmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 255) { Value = LockName });
                    var got = Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct));
                    if (got < 0) throw new InvalidOperationException($"Could not take the migration lock (sp_getapplock returned {got}).");
                }

                // Another instance may have migrated while this one waited.
                pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count == 0)
                {
                    log.LogInformation("Database schema was brought up to date by another instance");
                    return;
                }
                log.LogWarning("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(ct);
                log.LogWarning("Database migrations applied: {Migrations}", string.Join(", ", pending));
            }
            finally
            {
                await db.Database.CloseConnectionAsync(); // releases the session lock
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex, "Database migration failed; the API will not start on an out-of-date schema");
            throw;
        }
    }
}
