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

    /// <summary>
    /// What an interrupted catalogue check had finished, kept on disk next to the tables it read, so a failure or a
    /// restart carries on from the first table that did not finish, or straight to the VERITA comparison.
    /// </summary>
    private sealed class Checkpoint
    {
        public DateTime StartedAt { get; set; }
        public List<string> Steps { get; set; } = [];
        public bool Extracted { get; set; }
        public List<string> Warnings { get; set; } = [];
        public int Attempts { get; set; }
        public DateTime? FailedAt { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Checks this process has already tried: after a restart an interrupted check resumes at once, then waits between tries.</summary>
    private readonly HashSet<Guid> _tried = [];
    private static readonly TimeSpan CheckpointLife = TimeSpan.FromDays(2);

    private string WorkDir(SyncHospital h) => Path.Combine(store.Options.WorkDir, "catalogue", h.Code);
    private string CheckpointPath(SyncHospital h) => Path.Combine(WorkDir(h), "checkpoint.json");

    private Checkpoint? Load(SyncHospital h)
    {
        try
        {
            if (!File.Exists(CheckpointPath(h))) return null;
            var cp = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(CheckpointPath(h)), Json);
            if (cp is not null && DateTime.UtcNow - cp.StartedAt < CheckpointLife) return cp;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Unreadable catalogue checkpoint for {Hospital}; starting over.", h.Code);
        }
        Discard(h);   // too old to trust: the HIS has moved on
        return null;
    }

    private void Save(SyncHospital h, Checkpoint cp)
    {
        Directory.CreateDirectory(WorkDir(h));
        var tmp = CheckpointPath(h) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cp, Json));
        File.Move(tmp, CheckpointPath(h), overwrite: true);
    }

    private void Discard(SyncHospital h)
    {
        try { if (Directory.Exists(WorkDir(h))) Directory.Delete(WorkDir(h), recursive: true); }
        catch (IOException ex) { log.LogWarning(ex, "Could not remove {Dir}", WorkDir(h)); }
    }

    private async Task HospitalAsync(SyncHospital h, CancellationToken ct)
    {
        await using var db = await store.DbAsync(ct);
        var setting = await db.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
        var requested = setting?.CatalogueRequestedAt is not null;
        var due = setting is { CatalogueEnabled: true } && DailyDue(setting);
        var cp = Load(h);
        var retryAt = cp?.FailedAt?.AddMinutes(store.Options.CatalogueRetryMinutes);
        var resume = cp is not null && (!_tried.Contains(h.Id) || retryAt is null || DateTime.UtcNow >= retryAt);
        if (!requested && !due && !resume)
        {
            await store.WorkerAsync(h.Id, Kind, w =>
            {
                w.Status = cp is not null ? "PAUSED" : "IDLE";
                w.Step = cp is not null
                    ? $"Resumes the interrupted check at {retryAt!.Value.AddHours(store.Options.UtcOffsetHours):HH:mm} ({cp.Steps.Count} of {CatalogueExtract.Steps.Length} HIS tables read)"
                    : setting is { CatalogueEnabled: true } ? $"Next check at {setting.CatalogueTime:HH:mm}" : "Daily check is off; use Sync now";
                if (cp is null) { w.BatchesDone = 0; w.BatchesTotal = null; w.RecordsDone = 0; w.RecordsTotal = null; }
            }, ct);
            return;
        }

        // The request is taken now; from here an interruption is carried by the checkpoint, not by the request.
        if (setting is not null && requested)
        {
            setting.CatalogueRequestedAt = null;
            setting.CatalogueRequestedBy = null;
            await db.SaveChangesAsync(ct);
        }
        _tried.Add(h.Id);
        var resuming = cp is not null;
        cp ??= new Checkpoint { StartedAt = DateTime.UtcNow };
        Save(h, cp);
        await store.WorkerAsync(h.Id, Kind, w =>
        {
            w.Status = "RUNNING"; w.StartedAt = DateTime.UtcNow; w.LastError = null;
            w.Step = resuming ? "Resuming the interrupted check" : "Reading the HIS catalogue";
            w.BatchesDone = cp.Steps.Count; w.BatchesTotal = CatalogueExtract.Steps.Length; w.RecordsDone = 0; w.RecordsTotal = null;
        }, ct);
        if (resuming)
            await store.LogAsync(h.Id, Kind, "INFO", cp.Extracted
                ? "Catalogue check resumed: the HIS catalogue was already read; comparing with VERITA again."
                : $"Catalogue check resumed: {cp.Steps.Count} of {CatalogueExtract.Steps.Length} HIS tables were already read; carrying on from there.", ct: ct);

        var finished = false;
        try
        {
            await CheckAsync(h, cp, ct);
            finished = true;
            Discard(h);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "IDLE"; w.FinishedAt = DateTime.UtcNow; w.Step = "Check finished"; }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HisUnavailableException ex)
        {
            // Not counted as a try: the HIS never answered. The checkpoint waits and the check carries on when it does.
            cp.FailedAt = DateTime.UtcNow;
            cp.Error = ex.Message;
            Save(h, cp);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "PAUSED"; w.LastError = ex.Message; w.Step = "Cannot reach the HIS; the check resumes when it answers"; }, ct);
            await store.LogAsync(h.Id, Kind, "WARN", $"Catalogue check waiting: cannot reach the HIS ({ex.Message.TrimEnd('.')}). It resumes from where it stopped.", ct: ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Catalogue check for {Hospital} failed.", h.Code);
            cp.Attempts++;
            cp.FailedAt = DateTime.UtcNow;
            cp.Error = SyncStore.Clip(ex.Message, 2000);
            var giveUp = cp.Attempts >= store.Options.MaxAttempts;
            if (giveUp)
            {
                finished = true;
                Discard(h);
            }
            else Save(h, cp);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "FAILED"; w.FinishedAt = DateTime.UtcNow; w.LastError = ex.Message; w.Step = giveUp ? "Check failed" : "Check stopped; it resumes from where it stopped"; }, ct);
            await store.LogAsync(h.Id, Kind, giveUp ? "ERROR" : "WARN",
                giveUp
                    ? $"Catalogue check failed {cp.Attempts} times and was given up: {ex.Message.TrimEnd('.')}. The next daily check or Sync now starts over."
                    : $"Catalogue check stopped (try {cp.Attempts} of {store.Options.MaxAttempts}): {ex.Message.TrimEnd('.')}. It resumes from there in {store.Options.CatalogueRetryMinutes} minutes, or at once after a restart.",
                ex.ToString(), ct: ct);
        }
        finally
        {
            // Only a finished (or given-up) check counts as today's; an interrupted one carries on from its checkpoint.
            if (finished)
            {
                await using var db2 = await store.DbAsync(ct);
                var s2 = await db2.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
                if (s2 is not null) { s2.CatalogueLastRunAt = DateTime.UtcNow; await db2.SaveChangesAsync(ct); }
            }
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

    private async Task CheckAsync(SyncHospital h, Checkpoint cp, CancellationToken ct)
    {
        var s = store.SettingsFor(h);
        var zip = Path.Combine(WorkDir(h), $"{s.PackPrefix}-catalogue.zip");
        if (!cp.Extracted || !File.Exists(zip))
        {
            cp.Extracted = false;
            await using var his = await SyncStore.OpenHisAsync(s);
            await SyncStore.StageAsync("Reading the HIS catalogue", async () =>
            {
                // resume: keep the tables an interrupted run finished; each other table is cleared and read again.
                using var pack = new CsvZipWriter(zip, PackRunner.Readme(s, "catalogue", "Catalogue check"), s, resume: true);
                try
                {
                    await CatalogueExtract.RunAsync(his, s, pack, cp.Steps.ToHashSet(), async step =>
                    {
                        cp.Steps.Add(step);
                        Save(h, cp);
                        await store.WorkerAsync(h.Id, Kind, w => { w.Step = $"Read {step} from HIS"; w.BatchesDone = cp.Steps.Count; }, ct);
                    });
                }
                catch (Exception ex)
                {
                    pack.Fail(ex);
                    throw;
                }
                ct.ThrowIfCancellationRequested();
                cp.Warnings.AddRange(pack.Warnings);
                pack.Completed = true;
                return true;
            });
            cp.Extracted = true;
            Save(h, cp);
        }
        var pack = new Pack(Path.GetFileName(zip), await File.ReadAllBytesAsync(zip, ct), cp.Warnings);
        if (pack.Warnings.Count > 0)
            await store.LogAsync(h.Id, Kind, "WARN", $"Catalogue check: {pack.Warnings.Count} HIS step(s) failed and were skipped.", string.Join("; ", pack.Warnings), ct: ct);

        await store.WorkerAsync(h.Id, Kind, w => w.Step = "Comparing with VERITA", ct);
        var results = await SyncStore.StageAsync("Comparing with VERITA (dry-run import)", () => packs.ImportAsync(pack, h.Code, dryRun: true, ct));
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
        var fileId = await SyncStore.StageAsync("Keeping the catalogue pack in VERITA", () => packs.KeepAsync(pack, ct));
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
