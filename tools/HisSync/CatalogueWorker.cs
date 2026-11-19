using System.Text.Json;
using HisExtract;
using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;
using Verita.Infrastructure.DataHub;

namespace Verita.HisSync;

/// <summary>
/// Checks each hospital's HIS catalogue once a day at the time set on HIS Sync, or at once on "Sync now". A check
/// extracts the catalogue and validates it against VERITA without writing (a dry-run import). New or changed rows
/// are parked: the pack is kept as an import file and listed on HIS Sync, where a person applies or discards it.
/// </summary>
public sealed class CatalogueWorker(SyncStore store, PackRunner packs, IServiceScopeFactory scopes, ILogger<CatalogueWorker> log) : BackgroundService
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

        // First catalogue, applied straight into VERITA in batches: where it is, and what each table gave so far.
        public bool Applying { get; set; }
        public int ApplyTable { get; set; }
        public int ApplyRow { get; set; }
        public Guid? ChangeId { get; set; }
        public List<TableTotal> Tables { get; set; } = [];
    }

    private sealed class TableTotal
    {
        public string Entity { get; set; } = "";
        public int Rows { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Rejected { get; set; }
        public List<string> Rejects { get; set; } = [];
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
            // A first catalogue part-applied must always be finished, however long it has waited.
            if (cp is not null && (cp.Applying || DateTime.UtcNow - cp.StartedAt < CheckpointLife)) return cp;
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
            w.Step = !resuming ? "Reading the HIS catalogue" : cp.Applying ? $"Resuming the first catalogue at {Where(cp)}" : "Resuming the interrupted check";
            w.BatchesDone = cp.Steps.Count; w.BatchesTotal = CatalogueExtract.Steps.Length; w.RecordsDone = 0; w.RecordsTotal = null;
        }, ct);
        if (resuming)
            await store.LogAsync(h.Id, Kind, "INFO", cp.Applying
                ? $"First catalogue resumed at {Where(cp)}."
                : cp.Extracted
                ? "Catalogue check resumed: the HIS catalogue was already read; carrying on with the VERITA step."
                : $"Catalogue check resumed: {cp.Steps.Count} of {CatalogueExtract.Steps.Length} HIS tables were already read; carrying on from there.", ct: ct);

        var finished = false;
        try
        {
            await store.WithHeartbeatAsync(h.Id, Kind, () => CheckAsync(h, cp, ct), ct);
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
            // A part-applied first catalogue is never abandoned (that would leave half a catalogue): it keeps retrying.
            var giveUp = cp.Attempts >= store.Options.MaxAttempts && !cp.Applying;
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
                    : cp.Applying
                        ? $"First catalogue stopped at {Where(cp)} (try {cp.Attempts}): {ex.Message.TrimEnd('.')}. It carries on from that batch in {store.Options.CatalogueRetryMinutes} minutes, or at once after a restart."
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

        // First catalogue: VERITA holds nothing for this hospital, so every row is new and a dry-run (the slowest step)
        // would only say so. Count the pack and park it; rows that fail VERITA's checks show when it is applied.
        // First catalogue: VERITA holds nothing for this hospital, so nothing needs comparing or reviewing. Apply it
        // straight away, table by table in small committed batches, recording each batch, so a failure or a restart
        // carries on from the batch that did not finish.
        bool first = cp.Applying;
        if (!first)
            await using (var vdb = await store.DbAsync(ct))
                first = !await vdb.Departments.AnyAsync(x => x.HospitalId == h.Id, ct) && !await vdb.Procedures.AnyAsync(x => x.HospitalId == h.Id, ct);
        if (first)
        {
            await ApplyFirstAsync(h, cp, pack, ct);
            return;
        }
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

    private static string Where(Checkpoint cp) =>
        cp.Tables.Count == 0 ? "the start" : $"table {Math.Min(cp.ApplyTable + 1, cp.Tables.Count)} of {cp.Tables.Count} ({cp.Tables[Math.Min(cp.ApplyTable, cp.Tables.Count - 1)].Entity}), row {cp.ApplyRow + 1:N0}";

    /// <summary>
    /// The hospital's first catalogue, applied without review: the pack is mapped once, then each table is written in
    /// batches of CatalogueBatchRows, each batch its own transaction. The checkpoint records the table and row after
    /// every batch, so a failure or restart repeats only the batch in hand. Facts wait until the last batch is in.
    /// </summary>
    private async Task ApplyFirstAsync(SyncHospital h, Checkpoint cp, Pack pack, CancellationToken ct)
    {
        var batchRows = Math.Max(100, store.Options.CatalogueBatchRows);
        await using var scope = scopes.CreateAsyncScope();
        var his = scope.ServiceProvider.GetRequiredService<HisImportService>();
        var hub = scope.ServiceProvider.GetRequiredService<DataHubService>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await store.WorkerAsync(h.Id, Kind, w => w.Step = "First catalogue: reading the pack", ct);
        var prepared = await SyncStore.StageAsync("Reading the catalogue pack", () =>
            his.PrepareAsync(SyncStore.System, pack.FileName, new MemoryStream(pack.Bytes, writable: false), h.Code, dryRun: false, null, ct));
        var tables = prepared.Set;
        var totalRows = tables.Sum(t => t.Rows.Count);

        if (!cp.Applying)
        {
            cp.Applying = true;
            cp.ApplyTable = 0;
            cp.ApplyRow = 0;
            cp.Tables = tables.Select(t => new TableTotal { Entity = t.Spec.Name, Rows = t.Rows.Count }).ToList();
            await using (var db = await store.DbAsync(ct))
            {
                var change = new HisSyncCatalogueChange
                {
                    Id = Guid.NewGuid(), HospitalId = h.Id, DetectedAt = DateTime.UtcNow, Status = "APPLYING",
                    TablesJson = JsonSerializer.Serialize(cp.Tables.Select(t => new { t.Entity, t.Rows, Created = 0, Updated = 0, Unchanged = 0, Rejected = 0 }), Json),
                };
                db.HisSyncCatalogueChanges.Add(change);
                await db.SaveChangesAsync(ct);
                cp.ChangeId = change.Id;
            }
            Save(h, cp);
            await store.LogAsync(h.Id, Kind, "INFO",
                $"First catalogue for {h.Code}: applying {totalRows:N0} row(s) in {tables.Count} table(s) straight into VERITA, in batches of {batchRows:N0}. Facts wait until it is in.", ct: ct);
        }

        var done = cp.Tables.Take(cp.ApplyTable).Sum(t => t.Rows) + cp.ApplyRow;
        for (var ti = cp.ApplyTable; ti < tables.Count; ti++)
        {
            var (spec, rows) = tables[ti];
            var total = cp.Tables[ti];
            for (var offset = cp.ApplyRow; offset < rows.Count; offset += batchRows)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = rows.GetRange(offset, Math.Min(batchRows, rows.Count - offset));
                var label = $"{spec.Name} rows {offset + 1:N0}–{offset + chunk.Count:N0} of {rows.Count:N0}";
                var doneSoFar = done;
                await store.WorkerAsync(h.Id, Kind, w =>
                {
                    w.Step = $"First catalogue: {label}";
                    w.BatchesDone = ti; w.BatchesTotal = tables.Count; w.RecordsDone = doneSoFar; w.RecordsTotal = totalRows;
                }, ct);
                var r = (await SyncStore.StageAsync($"Applying {label}", () =>
                    hub.ImportSetAsync([(spec, chunk)], SyncStore.System, dryRun: false, prepared.Source, ct, prepared.Hospital))).Single();
                total.Created += r.Created; total.Updated += r.Updated; total.Unchanged += r.Unchanged; total.Rejected += r.Rejected;
                foreach (var e in r.Rejects.Take(Math.Max(0, 5 - total.Rejects.Count)))
                    total.Rejects.Add($"[{e.Key ?? $"row {offset + e.Row}"}] {string.Join("; ", e.Errors)}");
                done += chunk.Count;
                cp.ApplyRow = offset + chunk.Count;
                cp.Attempts = 0;
                Save(h, cp);
            }
            await store.LogAsync(h.Id, Kind, total.Rejected > 0 ? "WARN" : "INFO",
                $"First catalogue: {spec.Name} applied, {total.Rows:N0} row(s): {total.Created:N0} new, {total.Updated:N0} changed{(total.Rejected > 0 ? $", {total.Rejected:N0} rejected by VERITA's checks" : "")}.",
                total.Rejected > 0 ? string.Join(" ", total.Rejects) : null, ct: ct);
            cp.ApplyTable = ti + 1;
            cp.ApplyRow = 0;
            Save(h, cp);
        }

        // All in: record it with the other checks, and audit it as one import.
        var results = cp.Tables.Select(t => new Verita.Infrastructure.DataHub.ImportResult(t.Entity, null, false, t.Rows, t.Created, t.Updated, t.Unchanged, t.Rejected, [])).ToArray();
        await using (var db = await store.DbAsync(ct))
        {
            var change = cp.ChangeId is { } id ? await db.HisSyncCatalogueChanges.FirstOrDefaultAsync(c => c.Id == id, ct) : null;
            if (change is not null)
            {
                change.Status = "APPLIED";
                change.Created = results.Sum(x => x.Created); change.Updated = results.Sum(x => x.Updated);
                change.Unchanged = results.Sum(x => x.Unchanged); change.Rejected = results.Sum(x => x.Rejected);
                change.TablesJson = JsonSerializer.Serialize(results.Select(x => new { x.Entity, x.Rows, x.Created, x.Updated, x.Unchanged, x.Rejected }), Json);
                change.DecidedAt = DateTime.UtcNow;
                change.DecidedBy = SyncStore.Actor;
                await db.SaveChangesAsync(ct);
            }
        }
        await his.FinishAsync(SyncStore.System, prepared, dryRun: false, results, sw.ElapsedMilliseconds, ct);
        await store.WorkerAsync(h.Id, Kind, w => { w.BatchesDone = tables.Count; w.RecordsDone = totalRows; }, ct);
        await store.LogAsync(h.Id, Kind, results.Sum(x => x.Rejected) > 0 ? "WARN" : "INFO",
            $"First catalogue applied: {totalRows:N0} row(s) in {tables.Count} table(s), {results.Sum(x => x.Created):N0} new{(results.Sum(x => x.Rejected) > 0 ? $", {results.Sum(x => x.Rejected):N0} rejected" : "")}. Facts can sync now.", ct: ct);
    }

    private static string Rejects(IEnumerable<Verita.Infrastructure.DataHub.ImportResult> results) =>
        string.Join(" ", results.Where(r => r.Rejected > 0).Select(r =>
            $"{r.Entity}: {r.Rejected} rejected ({string.Join("; ", r.Rejects.Take(3).Select(e => $"[{e.Key ?? $"row {e.Row}"}] {string.Join(", ", e.Errors)}"))})."));
}
