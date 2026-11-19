using System.Text;
using HisExtract;
using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;
using Verita.Infrastructure.DataHub;

namespace Verita.HisSync;

/// <summary>
/// Syncs the fact dates queued on the HIS Sync screen, oldest first, straight into VERITA (no parking).
/// Each pass does one small unit per hospital: list a day's encounters, sync one batch of them, or write the
/// day's own refunds. Progress lives in the database, so a recycle or crash only repeats the unit in hand.
/// </summary>
public sealed class FactsWorker(SyncStore store, PackRunner packs, ILogger<FactsWorker> log) : BackgroundService
{
    private const string Kind = "FACTS";
    private static readonly string[] Finished = ["DONE", "DONE_WITH_ERRORS", "FAILED"];
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(6);
    private readonly Dictionary<Guid, (DateTime At, Dictionary<string, object> Values)> _caches = [];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        log.LogInformation("Facts worker started: batches of {Size} encounters, {Pause} ms between batches.", store.Options.BatchSize, store.Options.PauseBetweenBatchesMs);
        while (!stop.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                if (store.Options.Enabled && store.Options.FactsEnabled && await store.SchemaReadyAsync(stop))
                {
                    await using var held = await store.TryLockAsync("verita-his-sync-facts", stop);
                    if (held is null) log.LogInformation("Another HIS sync process holds the facts lock; waiting.");
                    else
                        foreach (var h in await store.HospitalsAsync(stop))
                            worked |= await HospitalAsync(h, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Facts worker pass failed."); }

            try { await Task.Delay(worked ? store.Options.PauseBetweenBatchesMs : store.Options.PollSeconds * 1000, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One unit of work for one hospital. True when something was done (so the next pass comes sooner).</summary>
    private async Task<bool> HospitalAsync(SyncHospital h, CancellationToken ct)
    {
        try
        {
            return await StepAsync(h, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Facts sync for {Hospital} failed.", h.Code);
            await store.WorkerAsync(h.Id, Kind, w => { w.Status = "FAILED"; w.LastError = ex.Message; }, ct);
            await store.LogAsync(h.Id, Kind, "ERROR", $"Facts sync stopped: {ex.Message}", ex.ToString(), ct: ct);
            return false;
        }
    }

    private async Task<bool> StepAsync(SyncHospital h, CancellationToken ct)
    {
        await using var db = await store.DbAsync(ct);
        var range = await db.HisSyncRanges
            .Where(r => r.HospitalId == h.Id && (r.Status == "QUEUED" || r.Status == "RUNNING"))
            .OrderBy(r => r.FromDate).ThenBy(r => r.RequestedAt).FirstOrDefaultAsync(ct);
        if (range is null)
        {
            await store.WorkerAsync(h.Id, Kind, w =>
            {
                if (w.Status == "RUNNING") w.FinishedAt = DateTime.UtcNow;
                w.Status = "IDLE"; w.Step = "Waiting for dates to sync"; w.RangeId = null; w.CurrentDate = null;
                w.BatchesDone = 0; w.BatchesTotal = null; w.RecordsDone = 0; w.RecordsTotal = null; w.LastError = null;
            }, ct);
            return false;
        }

        // Facts point at the catalogue's procedures, departments and doctors.
        if (!await db.Procedures.AnyAsync(p => p.HospitalId == h.Id, ct))
            return await PausedAsync(h, range, "Waiting for the catalogue: import or apply the hospital's catalogue first.", ct);
        if (await db.HisSyncCatalogueChanges.AnyAsync(c => c.HospitalId == h.Id && c.Status == "PENDING" && c.Created > 0, ct))
            return await PausedAsync(h, range, "Waiting for parked catalogue changes: apply or discard them on HIS Sync. New catalogue rows would otherwise reject facts.", ct);

        if (range.Status == "QUEUED")
        {
            range.Status = "RUNNING";
            range.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await store.LogAsync(h.Id, Kind, "INFO", $"Facts sync started for {Dates(range)}.", ct: ct);
        }

        var days = await db.HisSyncDays.Where(d => d.RangeId == range.Id).ToListAsync(ct);
        var done = days.Where(d => Finished.Contains(d.Status)).Select(d => d.Date).ToHashSet();
        DateOnly? next = null;
        for (var d = range.FromDate; d <= range.ToDate; d = d.AddDays(1))
            if (!done.Contains(d)) { next = d; break; }

        if (next is null)
        {
            var errors = days.Count(d => d.Status != "DONE");
            range.Status = errors > 0 ? "DONE_WITH_ERRORS" : "DONE";
            range.FinishedAt = DateTime.UtcNow;
            range.DaysDone = days.Count;
            range.Error = errors > 0 ? $"{errors} day{(errors == 1 ? "" : "s")} synced with errors." : null;
            await db.SaveChangesAsync(ct);
            await store.LogAsync(h.Id, Kind, errors > 0 ? "WARN" : "INFO",
                errors > 0 ? $"Facts {Dates(range)} finished: {days.Count - errors} day(s) synced, {errors} with errors." : $"Facts {Dates(range)} synced.", ct: ct);
            return true;
        }
        if (next.Value >= store.Today)
            return await PausedAsync(h, range, $"{next:yyyy-MM-dd} is today or later and is not synced yet.", ct);

        var day = days.FirstOrDefault(d => d.Date == next.Value);
        if (day is null)
        {
            day = new HisSyncDay { Id = Guid.NewGuid(), HospitalId = h.Id, RangeId = range.Id, Date = next.Value };
            db.HisSyncDays.Add(day);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            if (day.Status == "PENDING") return await ListDayAsync(db, h, range, day, ct);
            return await BatchAsync(db, h, range, day, ct);
        }
        catch (HisUnavailableException ex)
        {
            // Not the day's fault: nothing is counted as a try, and the day waits until the HIS answers again.
            return await PausedAsync(h, range, $"Cannot reach the HIS ({ex.Message}). Retrying every {store.Options.PollSeconds} s.", ct);
        }
    }

    // ── A day's encounter list ───────────────────────────────────────────────────

    private async Task<bool> ListDayAsync(Verita.Infrastructure.Data.VeritaDbContext db, SyncHospital h, HisSyncRange range, HisSyncDay day, CancellationToken ct)
    {
        await Progress(h, range, day, $"Listing the encounters of {day.Date:yyyy-MM-dd}", ct);
        day.StartedAt ??= DateTime.UtcNow;
        FactsManifest manifest;
        try
        {
            var s = Day(h, day.Date);
            await using var his = await SyncStore.OpenHisAsync(s);
            manifest = await SyncStore.StageAsync("Reading the day's encounters from HIS", () => FactsManifest.BuildAsync(his, s));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HisUnavailableException)
        {
            day.Attempts++;
            day.Error = SyncStore.Clip(ex.Message, 2000);
            var final = day.Attempts >= store.Options.MaxAttempts;
            if (final)
            {
                day.Status = "FAILED";
                day.FinishedAt = DateTime.UtcNow;
                range.DaysDone++;
            }
            await db.SaveChangesAsync(ct);
            await store.LogAsync(h.Id, Kind, final ? "ERROR" : "WARN",
                final ? $"{day.Date:yyyy-MM-dd}: could not list the day's encounters after {day.Attempts} tries. The day is skipped." : $"{day.Date:yyyy-MM-dd}: listing the day's encounters failed (try {day.Attempts}); retrying.",
                ex.Message, day.Date, ct);
            return final;   // a retry waits the poll interval, so a slow moment on HIS is not counted three times at once
        }

        var links = manifest.ErLinks.ToDictionary(l => l.Assessment);
        var rows = new List<HisSyncEncounter>(manifest.Count);
        foreach (var (item, batch) in manifest.Batches(Math.Max(1, store.Options.BatchSize)))
        {
            var er = item.Type == "ER" && links.TryGetValue(item.No, out var link) ? link : null;
            rows.Add(new HisSyncEncounter
            {
                Id = Guid.NewGuid(), DayId = day.Id, Type = item.Type, HisNo = item.No, GroupNo = item.Group, BatchNo = batch,
                LinkAppointmentId = er?.AppointmentId, LinkInvoiceAppointment = er?.InvoiceAppointment, LinkInvoiceCompany = er?.InvoiceCompany,
            });
        }
        db.HisSyncEncounters.AddRange(rows);
        day.Encounters = rows.Count;
        day.Batches = rows.Count == 0 ? 0 : rows.Max(r => r.BatchNo);
        day.Status = "RUNNING";
        day.Attempts = 0;
        day.Error = null;
        await db.SaveChangesAsync(ct);
        await store.LogAsync(h.Id, Kind, "INFO",
            $"{day.Date:yyyy-MM-dd}: {rows.Count} encounters ({rows.Count(r => r.Type == "IP")} IP, {rows.Count(r => r.Type == "OP")} OP, {rows.Count(r => r.Type == "ER")} ER) in {day.Batches} batch{(day.Batches == 1 ? "" : "es")}.",
            factDate: day.Date, ct: ct);
        return true;
    }

    // ── One batch, then the day's refunds, then the day is done ─────────────────

    private async Task<bool> BatchAsync(Verita.Infrastructure.Data.VeritaDbContext db, SyncHospital h, HisSyncRange range, HisSyncDay day, CancellationToken ct)
    {
        var batchNo = await db.HisSyncEncounters.Where(e => e.DayId == day.Id && e.Status == "PENDING")
            .OrderBy(e => e.BatchNo).Select(e => (int?)e.BatchNo).FirstOrDefaultAsync(ct);
        if (batchNo is not null) return await RunBatchAsync(db, h, range, day, batchNo.Value, ct);
        if (!day.RefundsDone) return await RefundsAsync(db, h, range, day, ct);

        day.Status = day.Failed > 0 || day.Error is not null ? "DONE_WITH_ERRORS" : "DONE";
        day.FinishedAt = DateTime.UtcNow;
        range.DaysDone++;
        await db.SaveChangesAsync(ct);
        await store.LogAsync(h.Id, Kind, day.Status == "DONE" ? "INFO" : "WARN",
            $"{day.Date:yyyy-MM-dd} synced: {day.Synced} of {day.Encounters} encounters{(day.Failed > 0 ? $", {day.Failed} failed" : "")}{(day.Error is not null ? $" ({day.Error})" : "")}.",
            factDate: day.Date, ct: ct);
        return true;
    }

    private async Task<bool> RunBatchAsync(Verita.Infrastructure.Data.VeritaDbContext db, SyncHospital h, HisSyncRange range, HisSyncDay day, int batchNo, CancellationToken ct)
    {
        var rows = await db.HisSyncEncounters.Where(e => e.DayId == day.Id && e.Status == "PENDING" && e.BatchNo == batchNo).ToListAsync(ct);
        var pendingBatches = await db.HisSyncEncounters.Where(e => e.DayId == day.Id && e.Status == "PENDING").Select(e => e.BatchNo).Distinct().CountAsync(ct);
        var batchesDone = Math.Max(0, day.Batches - pendingBatches);
        await Progress(h, range, day, $"{day.Date:yyyy-MM-dd}: batch {batchesDone + 1} of {day.Batches} ({rows.Count} encounters)", ct, batchesDone);

        var batch = new FactBatch(
            rows.Where(r => r.Type == "IP").Select(r => r.HisNo).ToArray(),
            rows.Where(r => r.Type == "OP").Select(r => r.HisNo).ToArray(),
            rows.Where(r => r.Type == "ER").Select(r => r.HisNo).ToArray(),
            rows.Where(r => r.Type == "ER" && (r.LinkAppointmentId is not null || r.LinkInvoiceAppointment is not null))
                .Select(r => new ErLink(r.HisNo, r.LinkAppointmentId, r.LinkInvoiceAppointment, r.LinkInvoiceCompany)).ToArray());
        try
        {
            var s = Day(h, day.Date);
            s.Batch = batch;
            s.Part = FactsPart.Batch;
            var results = await ExtractAndImportAsync(h, s, $"batch {batchNo}", ct);
            foreach (var r in rows) { r.Status = "SYNCED"; r.SyncedAt = DateTime.UtcNow; r.Error = null; }
            day.Synced += rows.Count;
            await db.SaveChangesAsync(ct);
            if (results.Sum(r => r.Rejected) is var rejected and > 0)
                await store.LogAsync(h.Id, Kind, "WARN", $"{day.Date:yyyy-MM-dd} batch {batchNo}: {rejected} row(s) rejected by VERITA's checks.",
                    Rejects(results), day.Date, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HisUnavailableException)
        {
            return await BatchFailedAsync(db, h, day, batchNo, rows, ex, ct);
        }
    }

    /// <summary>
    /// Retry; after the last try split the batch in two (whole groups each), so one bad encounter cannot hold up
    /// the rest. A single group that still fails is marked failed and listed in the log, and the day moves on.
    /// </summary>
    private async Task<bool> BatchFailedAsync(Verita.Infrastructure.Data.VeritaDbContext db, SyncHospital h, HisSyncDay day, int batchNo,
        List<HisSyncEncounter> rows, Exception ex, CancellationToken ct)
    {
        var attempts = rows.Max(r => r.Attempts) + 1;
        foreach (var r in rows) { r.Attempts = attempts; r.Error = SyncStore.Clip(ex.Message, 2000); }
        var groups = rows.GroupBy(r => r.GroupNo).OrderBy(g => g.Key).ToList();
        string level, message;
        var retry = attempts < store.Options.MaxAttempts;
        if (retry)
        {
            level = "WARN";
            message = $"{day.Date:yyyy-MM-dd} batch {batchNo} failed (try {attempts}); retrying.";
        }
        else if (groups.Count > 1)
        {
            var newBatch = await db.HisSyncEncounters.Where(e => e.DayId == day.Id).MaxAsync(e => e.BatchNo, ct) + 1;
            foreach (var r in groups.Skip(groups.Count / 2).SelectMany(g => g)) r.BatchNo = newBatch;
            foreach (var r in rows) r.Attempts = 0;
            day.Batches++;
            level = "WARN";
            message = $"{day.Date:yyyy-MM-dd} batch {batchNo} failed {attempts} times; split in two to find the encounter that fails.";
        }
        else
        {
            foreach (var r in rows) r.Status = "FAILED";
            day.Failed += rows.Count;
            level = "ERROR";
            message = $"{day.Date:yyyy-MM-dd}: {string.Join(", ", rows.Select(r => $"{r.Type} {r.HisNo}"))} could not be synced after {attempts} tries.";
        }
        await db.SaveChangesAsync(ct);
        await store.LogAsync(h.Id, Kind, level, message, ex.Message, day.Date, ct);
        await store.WorkerAsync(h.Id, Kind, w => w.LastError = ex.Message, ct);
        return !retry;
    }

    private async Task<bool> RefundsAsync(Verita.Infrastructure.Data.VeritaDbContext db, SyncHospital h, HisSyncRange range, HisSyncDay day, CancellationToken ct)
    {
        await Progress(h, range, day, $"{day.Date:yyyy-MM-dd}: refunds dated that day", ct, day.Batches);
        var links = await db.HisSyncEncounters.AsNoTracking()
            .Where(e => e.DayId == day.Id && e.Type == "ER" && (e.LinkAppointmentId != null || e.LinkInvoiceAppointment != null))
            .Select(e => new ErLink(e.HisNo, e.LinkAppointmentId, e.LinkInvoiceAppointment, e.LinkInvoiceCompany)).ToArrayAsync(ct);
        try
        {
            var s = Day(h, day.Date);
            s.Batch = new FactBatch([], [], [], links);
            s.Part = FactsPart.DayRefunds;
            var results = await ExtractAndImportAsync(h, s, "refunds", ct);
            day.RefundsDone = true;
            day.Attempts = 0;
            await db.SaveChangesAsync(ct);
            if (results.Sum(r => r.Rejected) is var rejected and > 0)
                await store.LogAsync(h.Id, Kind, "WARN", $"{day.Date:yyyy-MM-dd} refunds: {rejected} row(s) rejected by VERITA's checks.", Rejects(results), day.Date, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HisUnavailableException)
        {
            day.Attempts++;
            var final = day.Attempts >= store.Options.MaxAttempts;
            if (final)
            {
                day.RefundsDone = true;
                day.Error = SyncStore.Clip($"Refunds dated that day were not synced: {ex.Message}", 2000);
            }
            await db.SaveChangesAsync(ct);
            await store.LogAsync(h.Id, Kind, final ? "ERROR" : "WARN",
                final ? $"{day.Date:yyyy-MM-dd}: refunds dated that day could not be synced after {day.Attempts} tries." : $"{day.Date:yyyy-MM-dd}: refunds failed (try {day.Attempts}); retrying.",
                ex.Message, day.Date, ct);
            return final;
        }
        return true;
    }

    private async Task<ImportResult[]> ExtractAndImportAsync(SyncHospital h, Settings s, string what, CancellationToken ct)
    {
        UseCache(h, s);
        Pack pack;
        await using (var his = await SyncStore.OpenHisAsync(s))
            pack = await SyncStore.StageAsync("Reading HIS", () => packs.ExtractAsync(s, $"{s.PackPrefix}-{s.From:yyyy-MM-dd}-facts.zip",
                PackRunner.Readme(s, "facts", $"Day {s.From:yyyy-MM-dd}, {what}"), p => FactsExtract.RunAsync(his, s, p), ct));
        KeepCache(h, s);
        // A skipped step would leave rows missing for good (the sync only moves forward): fail and retry instead.
        if (pack.Warnings.Count > 0) throw new InvalidOperationException("HIS steps failed: " + string.Join("; ", pack.Warnings));
        return await SyncStore.StageAsync("Importing into VERITA", () => packs.ImportAsync(pack, h.Code, dryRun: false, ct));
    }

    private Settings Day(SyncHospital h, DateOnly date)
    {
        var s = store.SettingsFor(h);
        s.From = date.ToDateTime(TimeOnly.MinValue);
        s.To = s.From.Value.AddDays(1);
        return s;
    }

    private void UseCache(SyncHospital h, Settings s)
    {
        if (!_caches.TryGetValue(h.Id, out var c) || DateTime.UtcNow - c.At > CacheLife) return;
        foreach (var (k, v) in c.Values) s.Cache[k] = v;
    }

    private void KeepCache(SyncHospital h, Settings s)
    {
        if (s.Cache.Count == 0) return;
        if (!_caches.TryGetValue(h.Id, out var c) || DateTime.UtcNow - c.At > CacheLife)
            _caches[h.Id] = c = (DateTime.UtcNow, new Dictionary<string, object>(StringComparer.Ordinal));
        foreach (var (k, v) in s.Cache) c.Values[k] = v;
    }

    private async Task<bool> PausedAsync(SyncHospital h, HisSyncRange range, string why, CancellationToken ct)
    {
        string? before = null;
        await store.WorkerAsync(h.Id, Kind, w =>
        {
            before = w.Status == "PAUSED" ? w.Step : null;
            w.Status = "PAUSED"; w.Step = why; w.RangeId = range.Id;
        }, ct);
        if (before != why) await store.LogAsync(h.Id, Kind, "WARN", $"Facts sync paused. {why}", ct: ct);
        return false;
    }

    private Task Progress(SyncHospital h, HisSyncRange range, HisSyncDay day, string step, CancellationToken ct, int? batchesDone = null) =>
        store.WorkerAsync(h.Id, Kind, w =>
        {
            if (w.Status != "RUNNING") w.StartedAt = DateTime.UtcNow;
            w.Status = "RUNNING"; w.Step = step; w.RangeId = range.Id; w.CurrentDate = day.Date;
            w.BatchesDone = batchesDone ?? 0; w.BatchesTotal = day.Batches > 0 ? day.Batches : null;
            w.RecordsDone = day.Synced + day.Failed; w.RecordsTotal = day.Encounters > 0 ? day.Encounters : null;
            w.LastError = null;
        }, ct);

    private static string Dates(HisSyncRange r) => r.FromDate == r.ToDate ? $"{r.FromDate:yyyy-MM-dd}" : $"{r.FromDate:yyyy-MM-dd} – {r.ToDate:yyyy-MM-dd}";

    private static string Rejects(ImportResult[] results)
    {
        var sb = new StringBuilder();
        foreach (var r in results.Where(r => r.Rejected > 0))
        {
            sb.Append($"{r.Entity}: {r.Rejected} rejected. ");
            foreach (var e in r.Rejects.Take(5)) sb.Append($"[{e.Key ?? $"row {e.Row}"}] {string.Join("; ", e.Errors)} ");
        }
        return sb.ToString().Trim();
    }
}
