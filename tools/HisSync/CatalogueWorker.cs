using System.Text.Json;
using HisExtract;
using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;

namespace Verita.HisSync;

/// <summary>
/// Checks each hospital's HIS catalogue once a day at the time set on HIS Sync, or at once on "Sync now". A check
/// extracts the catalogue and validates it against VERITA without writing (a dry-run import). New or changed rows
/// are parked: the pack is kept as an import file and listed on HIS Sync, where a person applies or discards it.
/// </summary>
public sealed class CatalogueWorker(SyncStore store, PackRunner packs, ILogger<CatalogueWorker> log) : BackgroundService
{
    private const string Kind = "CATALOGUE";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        log.LogInformation("Catalogue worker started.");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (store.Options.Enabled && store.Options.CatalogueEnabled && await store.SchemaReadyAsync(stop))
                {
                    await using var held = await store.TryLockAsync("verita-his-sync-catalogue", stop);
                    if (held is null) log.LogInformation("Another HIS sync process holds the catalogue lock; waiting.");
                    else
                        foreach (var h in await store.HospitalsAsync(stop))
                            await HospitalAsync(h, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Catalogue worker pass failed."); }

            try { await Task.Delay(store.Options.PollSeconds * 1000, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HospitalAsync(SyncHospital h, CancellationToken ct)
    {
        await using var db = await store.DbAsync(ct);
        var setting = await db.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
        var requested = setting?.CatalogueRequestedAt is not null;
        var due = setting is { CatalogueEnabled: true } && DailyDue(setting);
        if (!requested && !due)
        {
            await store.WorkerAsync(h.Id, Kind, w =>
            {
                w.Status = "IDLE";
                w.Step = setting is { CatalogueEnabled: true } ? $"Next check at {setting.CatalogueTime:HH:mm}" : "Daily check is off; use Sync now";
                w.BatchesDone = 0; w.BatchesTotal = null; w.RecordsDone = 0; w.RecordsTotal = null;
            }, ct);
            return;
        }

        // Clear the request first: a check that fails is logged, not retried in a loop.
        if (setting is not null)
        {
            setting.CatalogueRequestedAt = null;
            setting.CatalogueRequestedBy = null;
            await db.SaveChangesAsync(ct);
        }
        await store.WorkerAsync(h.Id, Kind, w => { w.Status = "RUNNING"; w.StartedAt = DateTime.UtcNow; w.Step = "Reading the HIS catalogue"; w.LastError = null; }, ct);

        try
        {
            await CheckAsync(h, ct);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "IDLE"; w.FinishedAt = DateTime.UtcNow; w.Step = "Check finished"; }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Catalogue check for {Hospital} failed.", h.Code);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "FAILED"; w.FinishedAt = DateTime.UtcNow; w.LastError = ex.Message; w.Step = "Check failed"; }, ct);
            await store.LogAsync(h.Id, Kind, "ERROR", $"Catalogue check failed: {ex.Message}", ex.ToString(), ct: ct);
        }
        finally
        {
            // A failed check still counts as today's, so the daily check does not repeat every minute.
            await using var db2 = await store.DbAsync(ct);
            var s2 = await db2.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
            if (s2 is not null) { s2.CatalogueLastRunAt = DateTime.UtcNow; await db2.SaveChangesAsync(ct); }
        }
    }

    /// <summary>On, past today's check time, and not yet checked today (hospital clock).</summary>
    private bool DailyDue(HisSyncSetting s)
    {
        var now = store.LocalNow;
        if (TimeOnly.FromDateTime(now) < s.CatalogueTime) return false;
        if (s.CatalogueLastRunAt is not { } last) return true;
        return DateOnly.FromDateTime(last.AddHours(store.Options.UtcOffsetHours)) < DateOnly.FromDateTime(now);
    }

    private async Task CheckAsync(SyncHospital h, CancellationToken ct)
    {
        var s = store.SettingsFor(h);
        Pack pack;
        await using (var his = await SyncStore.OpenHisAsync(s))
            pack = await packs.ExtractAsync(s, $"{s.PackPrefix}-catalogue.zip", PackRunner.Readme(s, "catalogue", "Catalogue check"),
                p => CatalogueExtract.RunAsync(his, s, p), ct);
        if (pack.Warnings.Count > 0)
            await store.LogAsync(h.Id, Kind, "WARN", $"Catalogue check: {pack.Warnings.Count} HIS step(s) failed and were skipped.", string.Join("; ", pack.Warnings), ct: ct);

        await store.WorkerAsync(h.Id, Kind, w => w.Step = "Comparing with VERITA", ct);
        var results = await packs.ImportAsync(pack, h.Code, dryRun: true, ct);
        int created = results.Sum(r => r.Created), updated = results.Sum(r => r.Updated), unchanged = results.Sum(r => r.Unchanged), rejected = results.Sum(r => r.Rejected);
        await store.WorkerAsync(h.Id, Kind, w => { w.RecordsDone = created + updated + unchanged + rejected; w.RecordsTotal = w.RecordsDone; }, ct);

        if (created + updated == 0)
        {
            await store.LogAsync(h.Id, Kind, rejected > 0 ? "WARN" : "INFO",
                rejected > 0 ? $"Catalogue checked: no new or changed rows; {rejected} HIS row(s) fail VERITA's checks." : $"Catalogue checked: no changes ({unchanged} rows).",
                rejected > 0 ? Rejects(results) : null, ct: ct);
            return;
        }

        // Park: keep the pack as an import file and list it. An older parked check is replaced by this one.
        await store.WorkerAsync(h.Id, Kind, w => w.Step = "Parking the changes for review", ct);
        var fileId = await packs.KeepAsync(pack, ct);
        await using var db = await store.DbAsync(ct);
        var older = await db.HisSyncCatalogueChanges.Where(c => c.HospitalId == h.Id && c.Status == "PENDING").ToListAsync(ct);
        foreach (var o in older)
        {
            o.Status = "SUPERSEDED";
            o.DecidedAt = DateTime.UtcNow;
            o.DecidedBy = SyncStore.Actor;
        }
        db.HisSyncCatalogueChanges.Add(new HisSyncCatalogueChange
        {
            Id = Guid.NewGuid(), HospitalId = h.Id, ImportFileId = fileId, DetectedAt = DateTime.UtcNow,
            Created = created, Updated = updated, Unchanged = unchanged, Rejected = rejected,
            TablesJson = JsonSerializer.Serialize(results.Select(r => new { r.Entity, r.Rows, r.Created, r.Updated, r.Unchanged, r.Rejected }), Json),
        });
        await db.SaveChangesAsync(ct);
        foreach (var o in older.Where(o => o.ImportFileId is not null))
        {
            try { await packs.DeleteKeptAsync(o.ImportFileId!.Value, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not remove superseded catalogue pack {File}", o.ImportFileId); }
        }
        await store.LogAsync(h.Id, Kind, "INFO",
            $"Catalogue changes found: {created} new, {updated} changed row(s). Parked for review on HIS Sync.",
            string.Join(", ", results.Where(r => r.Created + r.Updated > 0).Select(r => $"{r.Entity} +{r.Created} ~{r.Updated}"))
                + (rejected > 0 ? $". {rejected} row(s) fail VERITA's checks: {Rejects(results)}" : ""), ct: ct);
    }

    private static string Rejects(IEnumerable<Verita.Infrastructure.DataHub.ImportResult> results) =>
        string.Join(" ", results.Where(r => r.Rejected > 0).Select(r =>
            $"{r.Entity}: {r.Rejected} rejected ({string.Join("; ", r.Rejects.Take(3).Select(e => $"[{e.Key ?? $"row {e.Row}"}] {string.Join(", ", e.Errors)}"))})."));
}
