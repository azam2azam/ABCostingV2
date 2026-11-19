using HisExtract;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Verita.Application;
using Verita.Domain.Entities;
using Verita.Infrastructure.Runs;
using Verita.Infrastructure.Data;

namespace Verita.HisSync;

/// <summary>The HIS could not be reached (network, login, server down). Nothing was read, so nothing is marked failed.</summary>
public sealed class HisUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>A sync step failed in a named stage (reading HIS, importing into VERITA, ...).</summary>
public sealed class SyncStageException(string message, Exception inner) : Exception(message, inner);

/// <summary>A VERITA hospital linked to a HIS tenant (it was bound on its first HIS import).</summary>
public sealed record SyncHospital(Guid Id, string Code, string SetupId, short ProjectId)
{
    public string Tenant => $"{SetupId}-{ProjectId}";
}

/// <summary>What both workers share: the worker's identity, the clock, HIS settings, progress rows and the sync log.</summary>
public sealed class SyncStore(IDbContextFactory<VeritaDbContext> factory, VeritaConnection verita, IOptions<SyncOptions> sync,
    IOptions<HisOptions> his, ILogger<SyncStore> log)
{
    public const string Actor = "his-sync";

    /// <summary>
    /// The worker writes as a platform administrator scoped to every hospital. It is not a User: its id is empty, so
    /// audit entries carry no user id (AuditLog.UserId references User) and read "his-sync" as the actor.
    /// </summary>
    public static readonly Principal System = new(Guid.Empty, Actor, "HIS Sync worker", "ADMIN", "System", ["*"], ["*"]);

    public SyncOptions Options => sync.Value;

    public DateTime LocalNow => DateTime.UtcNow.AddHours(sync.Value.UtcOffsetHours);
    /// <summary>The hospital's date. Today is never synced.</summary>
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public async Task<List<SyncHospital>> HospitalsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Hospitals.AsNoTracking()
            .Where(h => h.IsActive && h.HisSetupId != null && h.HisSetupId != "" && h.HisProjectId != null)
            .OrderBy(h => h.Code)
            .Select(h => new { h.Id, h.Code, h.HisSetupId, h.HisProjectId }).ToListAsync(ct);
        var only = sync.Value.Hospitals.Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(r => r.HisProjectId is >= short.MinValue and <= short.MaxValue && (only.Count == 0 || only.Contains(r.Code)))
            .Select(r => new SyncHospital(r.Id, r.Code, r.HisSetupId!, (short)r.HisProjectId!.Value)).ToList();
    }

    /// <summary>Extract settings for the hospital's HIS tenant.</summary>
    public Settings SettingsFor(SyncHospital h)
    {
        var o = his.Value;
        o.Tenants.TryGetValue(h.Tenant, out var t);
        var cs = t?.ConnectionString is { Length: > 0 } own ? own : o.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"No HIS connection string for tenant {h.Tenant}. Set His:ConnectionString or His:Tenants:{h.Tenant}:ConnectionString.");
        return new Settings
        {
            ConnectionString = cs, SetupId = h.SetupId, ProjectId = h.ProjectId,
            PharmacyDb = t?.PharmacyDb ?? o.PharmacyDb, QueryTimeoutSeconds = o.QueryTimeoutSeconds, Claims = o.Claims,
            OutDir = sync.Value.WorkDir,
        };
    }

    /// <summary>Opens the HIS. A HIS that cannot be reached throws <see cref="HisUnavailableException"/>: wait, do not count a try.</summary>
    public static async Task<HisConnection> OpenHisAsync(Settings s)
    {
        var db = new HisConnection(s.ConnectionString) { QueryTimeoutSeconds = s.QueryTimeoutSeconds };
        try { await db.OpenAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await db.DisposeAsync();
            throw new HisUnavailableException(ex.Message, ex);
        }
        return db;
    }

    public async Task LogAsync(Guid hospitalId, string kind, string level, string message, string? detail = null, DateOnly? factDate = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.HisSyncLogs.Add(new HisSyncLog
            {
                Id = Guid.NewGuid(), HospitalId = hospitalId, At = DateTime.UtcNow, Kind = kind, Level = level,
                Message = Clip(message, 1000)!, Detail = Clip(detail, 4000), FactDate = factDate, Actor = Actor,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not write the sync log: {Message}", message);
        }
        var lvl = level switch { "ERROR" => LogLevel.Error, "WARN" => LogLevel.Warning, _ => LogLevel.Information };
        log.Log(lvl, "[{Kind}] {Message}", kind, message);
    }

    /// <summary>Update the worker's progress row for a hospital (created on first use) and touch its heartbeat.</summary>
    public async Task WorkerAsync(Guid hospitalId, string kind, Action<HisSyncWorker> change, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var w = await db.HisSyncWorkers.FirstOrDefaultAsync(x => x.HospitalId == hospitalId && x.Kind == kind, ct);
        if (w is null) db.HisSyncWorkers.Add(w = new HisSyncWorker { Id = Guid.NewGuid(), HospitalId = hospitalId, Kind = kind });
        change(w);
        w.HeartbeatAt = DateTime.UtcNow;
        w.LastError = Clip(w.LastError, 2000);
        w.Step = Clip(w.Step, 400);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs a long step (a HIS read, a catalogue dry-run, a batch import) while touching the worker's heartbeat every
    /// 30 seconds, so HIS Sync shows it working instead of "Not responding". Only the heartbeat is written.
    /// </summary>
    public async Task<T> WithHeartbeatAsync<T>(Guid hospitalId, string kind, Func<Task<T>> body, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var beat = Task.Run(async () =>
        {
            while (!stop.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
                    await using var db = await factory.CreateDbContextAsync(stop.Token);
                    await db.HisSyncWorkers.Where(w => w.HospitalId == hospitalId && w.Kind == kind)
                        .ExecuteUpdateAsync(u => u.SetProperty(w => w.HeartbeatAt, DateTime.UtcNow), stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log.LogWarning(ex, "Heartbeat update failed"); }
            }
        }, CancellationToken.None);
        try { return await body(); }
        finally
        {
            stop.Cancel();
            await beat;
        }
    }

    public Task WithHeartbeatAsync(Guid hospitalId, string kind, Func<Task> body, CancellationToken ct) =>
        WithHeartbeatAsync(hospitalId, kind, async () => { await body(); return true; }, ct);

    /// <summary>
    /// A lock in the VERITA database held for as long as the returned handle lives. IIS starts the new process before
    /// it stops the old one on a recycle; the lock keeps two of them from syncing at once. Null when another holds it.
    /// </summary>
    public async Task<IAsyncDisposable?> TryLockAsync(string name, CancellationToken ct)
    {
        // Not pooled: a pooled connection stays open after Dispose and keeps a session lock with it, so the lock would
        // outlive the pass (or a stopped process's last connection). Named, so the holder shows in sys.dm_exec_sessions.
        var cs = new SqlConnectionStringBuilder(verita.Value) { Pooling = false, ApplicationName = $"VeritaHisSync lock {name} ({Environment.MachineName}, pid {Environment.ProcessId})" };
        var conn = new SqlConnection(cs.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource=@n, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r;";
        cmd.Parameters.AddWithValue("@n", name);
        var r = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (r >= 0) return conn;
        await conn.DisposeAsync();
        return null;
    }

    public async Task<VeritaDbContext> DbAsync(CancellationToken ct) => await factory.CreateDbContextAsync(ct);

    /// <summary>The worker needs the HIS sync tables; until the VERITA API has applied its migrations, it waits.</summary>
    public async Task<bool> SchemaReadyAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            _ = await db.HisSyncDays.AnyAsync(ct);
            _ = await db.HisSyncCatalogueChanges.AnyAsync(ct);
            return true;
        }
        catch (SqlException ex)
        {
            log.LogWarning("HIS sync tables are not in the VERITA database yet ({Message}). Run the schema script (deploy/iis/sql) against the VERITA database.", ex.Message);
            return false;
        }
    }

    /// <summary>Runs one stage of a sync step; a failure names the stage, so a timeout says whether HIS or VERITA was slow.</summary>
    public static async Task<T> StageAsync<T>(string stage, Func<Task<T>> body)
    {
        try { return await body(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not HisUnavailableException)
        {
            throw new SyncStageException($"{stage}: {ex.Message}", ex);
        }
    }

    public static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
