using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.DataHub;

namespace Verita.Infrastructure.HisSync;

public sealed record HisSyncSettingDto(bool CatalogueEnabled, string CatalogueTime, DateTime? CatalogueRequestedAt, string? CatalogueRequestedBy,
    DateTime? CatalogueLastRunAt, DateTime? UpdatedAt, string? UpdatedBy);

public sealed record HisSyncRangeDto(Guid Id, string FromDate, string ToDate, string Status, int DaysTotal, int DaysDone,
    DateTime RequestedAt, string RequestedBy, DateTime? StartedAt, DateTime? FinishedAt, string? Error, string Mode = "SYNC");

public sealed record HisSyncWorkerDto(string Kind, string Status, bool Alive, string? Step, Guid? RangeId, string? CurrentDate,
    int BatchesDone, int? BatchesTotal, int RecordsDone, int? RecordsTotal, DateTime? StartedAt, DateTime? HeartbeatAt,
    DateTime? FinishedAt, string? LastError);

public sealed record HisSyncLogDto(Guid Id, DateTime At, string Kind, string Level, string Message, string? Detail, string? FactDate, string Actor);

/// <summary>Sections: the fact parts the day holds ("encounters:2,billing:1"); Needs: the parts its last run read ("" = up to date, null = all).</summary>
public sealed record HisSyncDayDto(Guid Id, Guid RangeId, string Date, string Status, int Encounters, int Batches, int Synced, int Failed,
    DateTime? StartedAt, DateTime? FinishedAt, string? Error, string? Sections = null, string? Needs = null);

public sealed record HisSyncCatalogueTableDto(string Entity, int Rows, int Created, int Updated, int Unchanged, int Rejected);

/// <summary>A parked catalogue check, with the state of its import file once applied.</summary>
public sealed record HisSyncCatalogueChangeDto(Guid Id, DateTime DetectedAt, string Status, int Created, int Updated, int Unchanged, int Rejected,
    HisSyncCatalogueTableDto[] Tables, DateTime? DecidedAt, string? DecidedBy, Guid? ImportFileId, string? ImportStatus, string? ImportError);

public sealed record HisSyncDto(string Hospital, string HospitalName, string? HisSetupId, int? HisProjectId, string Today,
    HisSyncSettingDto Settings, HisSyncRangeDto[] Ranges, HisSyncWorkerDto[] Workers, HisSyncLogDto[] Log, int Errors,
    HisSyncDayDto[] Days, HisSyncCatalogueChangeDto[] CatalogueChanges);

public sealed record HisSyncSettingsBody(bool CatalogueEnabled, string CatalogueTime);
/// <summary>Resync: "Sync again" — the dates may already be synced; each day reads only the parts it lacks.</summary>
public sealed record HisSyncRangeBody(string FromDate, string ToDate, bool Resync = false);

/// <summary>
/// The VERITA side of HIS sync: settings, the catalogue "sync now" request and the fact date ranges a
/// worker picks up, plus the worker's progress and log for the HIS Sync screen. Nothing here talks to HIS.
/// </summary>
public sealed class HisSyncService(IDbContextFactory<VeritaDbContext> factory, IAuditWriter audit, ImportFileService files)
{
    /// <summary>A worker that has not touched its row for this long is shown as not running.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    public const int MaxRangeDays = 366;
    private static readonly string[] Covering = ["QUEUED", "RUNNING", "DONE", "DONE_WITH_ERRORS"];

    public async Task<HisSyncDto> GetAsync(Principal p, string hospital, string? level, int take, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var s = await db.HisSyncSettings.AsNoTracking().FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
        var ranges = await db.HisSyncRanges.AsNoTracking().Where(x => x.HospitalId == h.Id)
            .OrderByDescending(x => x.FromDate).ThenByDescending(x => x.RequestedAt).Take(200).ToListAsync(ct);
        var workers = await db.HisSyncWorkers.AsNoTracking().Where(x => x.HospitalId == h.Id).ToListAsync(ct);
        var logQuery = db.HisSyncLogs.AsNoTracking().Where(x => x.HospitalId == h.Id);
        if (!string.IsNullOrWhiteSpace(level)) logQuery = logQuery.Where(x => x.Level == level.ToUpperInvariant());
        var log = await logQuery.OrderByDescending(x => x.At).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        var since = DateTime.UtcNow.AddDays(-7);
        var errors = await db.HisSyncLogs.CountAsync(x => x.HospitalId == h.Id && x.Level == "ERROR" && x.At >= since, ct);
        var now = DateTime.UtcNow;
        var days = await db.HisSyncDays.AsNoTracking().Where(x => x.HospitalId == h.Id)
            .OrderByDescending(x => x.Date).Take(120).ToListAsync(ct);
        var changes = await db.HisSyncCatalogueChanges.AsNoTracking().Where(x => x.HospitalId == h.Id)
            .OrderByDescending(x => x.DetectedAt).Take(10).ToListAsync(ct);
        var fileIds = changes.Where(c => c.ImportFileId is not null).Select(c => c.ImportFileId!.Value).ToList();
        var fileState = await db.ImportFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Status, f.ErrorMessage }).ToDictionaryAsync(f => f.Id, ct);

        // The screen always shows both workers, connected or not.
        var shown = new[] { "CATALOGUE", "FACTS" }.Select(kind =>
        {
            var w = workers.FirstOrDefault(x => x.Kind == kind);
            if (w is null) return new HisSyncWorkerDto(kind, "NOT_CONNECTED", false, null, null, null, 0, null, 0, null, null, null, null, null);
            var alive = w.HeartbeatAt is { } hb && now - hb < HeartbeatTimeout;
            return new HisSyncWorkerDto(kind, alive ? w.Status : (w.Status == "RUNNING" ? "STALLED" : w.Status), alive, w.Step, w.RangeId,
                w.CurrentDate?.ToString("yyyy-MM-dd"), w.BatchesDone, w.BatchesTotal, w.RecordsDone, w.RecordsTotal,
                w.StartedAt, w.HeartbeatAt, w.FinishedAt, w.LastError);
        }).ToArray();

        return new HisSyncDto(h.Code, h.Name, h.HisSetupId, h.HisProjectId, Today().ToString("yyyy-MM-dd"),
            s is null
                ? new HisSyncSettingDto(false, "01:00", null, null, null, null, null)
                : new HisSyncSettingDto(s.CatalogueEnabled, s.CatalogueTime.ToString("HH:mm"), s.CatalogueRequestedAt, s.CatalogueRequestedBy,
                    s.CatalogueLastRunAt, s.UpdatedAt, s.UpdatedBy),
            ranges.Select(Dto).ToArray(), shown,
            log.Select(x => new HisSyncLogDto(x.Id, x.At, x.Kind, x.Level, x.Message, x.Detail, x.FactDate?.ToString("yyyy-MM-dd"), x.Actor)).ToArray(),
            errors,
            days.Select(d => new HisSyncDayDto(d.Id, d.RangeId, d.Date.ToString("yyyy-MM-dd"), d.Status, d.Encounters, d.Batches, d.Synced, d.Failed,
                d.StartedAt, d.FinishedAt, d.Error, d.Sections, d.Needs)).ToArray(),
            changes.Select(c =>
            {
                var f = c.ImportFileId is { } id && fileState.TryGetValue(id, out var x) ? x : null;
                return new HisSyncCatalogueChangeDto(c.Id, c.DetectedAt, c.Status, c.Created, c.Updated, c.Unchanged, c.Rejected,
                    System.Text.Json.JsonSerializer.Deserialize<HisSyncCatalogueTableDto[]>(c.TablesJson, Json) ?? [],
                    c.DecidedAt, c.DecidedBy, c.ImportFileId, f?.Status, f?.ErrorMessage);
            }).ToArray());
    }

    public async Task<HisSyncSettingDto> SaveSettingsAsync(Principal p, string hospital, HisSyncSettingsBody body, CancellationToken ct)
    {
        if (!TimeOnly.TryParseExact(body.CatalogueTime ?? "", ["HH:mm", "H:mm", "HH:mm:ss"], out var time))
            throw AppError.Invalid("Enter the catalogue check time as HH:mm, for example 01:00.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var s = await db.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
        var before = s is null ? null : new { s.CatalogueEnabled, CatalogueTime = s.CatalogueTime.ToString("HH:mm") };
        if (s is null) db.HisSyncSettings.Add(s = new HisSyncSetting { Id = Guid.NewGuid(), HospitalId = h.Id });
        s.CatalogueEnabled = body.CatalogueEnabled;
        s.CatalogueTime = new TimeOnly(time.Hour, time.Minute);
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedBy = p.Email;
        Log(db, h.Id, "CONFIG", "INFO",
            body.CatalogueEnabled ? $"Daily catalogue check turned on at {s.CatalogueTime:HH:mm}." : "Daily catalogue check turned off.", p.Email);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "UPDATE", "HisSyncSetting", h.Code, $"HIS sync · {h.Code}", before,
            new { s.CatalogueEnabled, CatalogueTime = s.CatalogueTime.ToString("HH:mm") }, ct: ct);
        return new HisSyncSettingDto(s.CatalogueEnabled, s.CatalogueTime.ToString("HH:mm"), s.CatalogueRequestedAt, s.CatalogueRequestedBy,
            s.CatalogueLastRunAt, s.UpdatedAt, s.UpdatedBy);
    }

    /// <summary>"Sync now": the worker runs a catalogue check at its next pass, whether or not the daily check is on.</summary>
    public async Task RequestCatalogueAsync(Principal p, string hospital, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var s = await db.HisSyncSettings.FirstOrDefaultAsync(x => x.HospitalId == h.Id, ct);
        if (s?.CatalogueRequestedAt is not null)
            throw AppError.Conflict("ALREADY_REQUESTED", $"A catalogue check was already requested at {s.CatalogueRequestedAt:yyyy-MM-dd HH:mm} UTC by {s.CatalogueRequestedBy}. It runs when the worker picks it up.");
        if (s is null) db.HisSyncSettings.Add(s = new HisSyncSetting { Id = Guid.NewGuid(), HospitalId = h.Id, UpdatedAt = DateTime.UtcNow, UpdatedBy = p.Email });
        s.CatalogueRequestedAt = DateTime.UtcNow;
        s.CatalogueRequestedBy = p.Email;
        Log(db, h.Id, "CATALOGUE", "INFO", "Catalogue check requested.", p.Email);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "CREATE", "HisSyncCatalogueRequest", h.Code, $"HIS catalogue check · {h.Code}", ct: ct);
    }

    /// <summary>Queue a fact date range. Today and later are refused; a date already queued or synced cannot be added again.</summary>
    public async Task<HisSyncRangeDto> AddRangeAsync(Principal p, string hospital, HisSyncRangeBody body, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(body.FromDate ?? "", "yyyy-MM-dd", out var from) || !DateOnly.TryParseExact(body.ToDate ?? "", "yyyy-MM-dd", out var to))
            throw AppError.Invalid("Enter From and To as dates (yyyy-MM-dd).");
        if (to < from) throw AppError.Invalid("To is before From.");
        var today = Today();
        if (to >= today) throw AppError.Invalid($"Today ({today:yyyy-MM-dd}) and later cannot be synced. The last date you can add is {today.AddDays(-1):yyyy-MM-dd}.");
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > MaxRangeDays) throw AppError.Invalid($"A range can hold at most {MaxRangeDays} days. Add longer periods in parts.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        // A first sync may not overlap any range; "Sync again" may cover synced dates, but not dates still queued or running.
        var blocking = body.Resync ? ["QUEUED", "RUNNING"] : Covering;
        var overlap = await db.HisSyncRanges.AsNoTracking()
            .Where(x => x.HospitalId == h.Id && blocking.Contains(x.Status) && x.FromDate <= to && x.ToDate >= from)
            .OrderBy(x => x.FromDate).FirstOrDefaultAsync(ct);
        if (overlap is not null)
            throw AppError.Conflict("RANGE_OVERLAP", overlap.Status is "DONE" or "DONE_WITH_ERRORS"
                ? $"{overlap.FromDate:yyyy-MM-dd} – {overlap.ToDate:yyyy-MM-dd} is already synced. Choose dates outside it, or use Sync again to read only what those days lack."
                : $"{overlap.FromDate:yyyy-MM-dd} – {overlap.ToDate:yyyy-MM-dd} is already queued. Wait for it to finish first.");

        var r = new HisSyncRange
        {
            Id = Guid.NewGuid(), HospitalId = h.Id, FromDate = from, ToDate = to, DaysTotal = days,
            RequestedAt = DateTime.UtcNow, RequestedBy = p.Email, Mode = body.Resync ? "RESYNC" : "SYNC",
        };
        db.HisSyncRanges.Add(r);
        Log(db, h.Id, "FACTS", "INFO", body.Resync
            ? $"Facts {from:yyyy-MM-dd} – {to:yyyy-MM-dd} added to sync again ({days} day{(days == 1 ? "" : "s")}; each day reads only what it lacks)."
            : $"Facts {from:yyyy-MM-dd} – {to:yyyy-MM-dd} added to sync ({days} day{(days == 1 ? "" : "s")}).", p.Email);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "CREATE", "HisSyncRange", r.Id.ToString(),
            $"HIS facts {(body.Resync ? "sync again " : "")}{from:yyyy-MM-dd} – {to:yyyy-MM-dd} · {h.Code}", ct: ct);
        return Dto(r);
    }

    /// <summary>
    /// Remove a range the worker has not started: the record is deleted. The delete only matches a range still
    /// queued and untouched, so a range the worker picks up at the same moment is refused, not lost mid-sync.
    /// </summary>
    public async Task RemoveRangeAsync(Principal p, string hospital, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var r = await db.HisSyncRanges.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.HospitalId == h.Id, ct) ?? throw AppError.NotFound("Sync range");
        var deleted = await db.HisSyncRanges
            .Where(x => x.Id == id && x.HospitalId == h.Id && x.Status == "QUEUED" && x.StartedAt == null && x.DaysDone == 0)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw AppError.Conflict("RANGE_STARTED", "The sync for these dates has started, so they cannot be removed.");
        Log(db, h.Id, "FACTS", "INFO", $"Facts {r.FromDate:yyyy-MM-dd} – {r.ToDate:yyyy-MM-dd} removed from sync.", p.Email);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "DELETE", "HisSyncRange", r.Id.ToString(), $"HIS facts {r.FromDate:yyyy-MM-dd} – {r.ToDate:yyyy-MM-dd} · {h.Code}",
            before: new { FromDate = r.FromDate.ToString("yyyy-MM-dd"), ToDate = r.ToDate.ToString("yyyy-MM-dd"), r.Status, r.RequestedBy }, ct: ct);
    }

    /// <summary>
    /// Apply a parked catalogue check: its pack is queued for import like any Data Import file. The caller puts the
    /// returned job on the import queue. Facts sync, paused while new catalogue rows wait, resumes once it is decided.
    /// </summary>
    public async Task<ImportFileJob> ApplyCatalogueAsync(Principal p, string hospital, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var c = await db.HisSyncCatalogueChanges.FirstOrDefaultAsync(x => x.Id == id && x.HospitalId == h.Id, ct) ?? throw AppError.NotFound("Catalogue change");
        if (c.Status != "PENDING") throw AppError.Conflict("CHANGE_DECIDED", $"These catalogue changes are already {c.Status.ToLowerInvariant()}.");
        if (c.ImportFileId is not { } fileId) throw AppError.Invalid("The catalogue pack for these changes is missing. Run Sync now for a new check.");
        var job = await files.QueueAsync(p, fileId, h.Code, dryRun: false, ct);
        c.Status = "APPLIED";
        c.DecidedAt = DateTime.UtcNow;
        c.DecidedBy = p.Email;
        Log(db, h.Id, "CATALOGUE", "INFO", $"Catalogue changes applied ({c.Created} new, {c.Updated} changed row(s)); import queued.", p.Email);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "UPDATE", "HisSyncCatalogueChange", c.Id.ToString(), $"HIS catalogue changes · {h.Code}",
            new { Status = "PENDING" }, new { c.Status, c.ImportFileId }, ct: ct);
        return job;
    }

    /// <summary>Discard a parked catalogue check. Its pack is removed; the next check finds the same changes again if they are still there.</summary>
    public async Task DiscardCatalogueAsync(Principal p, string hospital, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await HospitalAsync(db, p, hospital, ct);
        var c = await db.HisSyncCatalogueChanges.FirstOrDefaultAsync(x => x.Id == id && x.HospitalId == h.Id, ct) ?? throw AppError.NotFound("Catalogue change");
        if (c.Status != "PENDING") throw AppError.Conflict("CHANGE_DECIDED", $"These catalogue changes are already {c.Status.ToLowerInvariant()}.");
        c.Status = "DISCARDED";
        c.DecidedAt = DateTime.UtcNow;
        c.DecidedBy = p.Email;
        Log(db, h.Id, "CATALOGUE", "INFO", $"Catalogue changes discarded ({c.Created} new, {c.Updated} changed row(s)).", p.Email);
        await db.SaveChangesAsync(ct);
        if (c.ImportFileId is { } fileId)
        {
            try { await files.DeleteAsync(p, fileId, ct); }
            catch (AppError) { /* already removed */ }
        }
        await audit.WriteAsync(p, "UPDATE", "HisSyncCatalogueChange", c.Id.ToString(), $"HIS catalogue changes · {h.Code}",
            new { Status = "PENDING" }, new { c.Status }, ct: ct);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The hospital's date. Hospitals run on Saudi time (UTC+3, no daylight saving).</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static async Task<Hospital> HospitalAsync(VeritaDbContext db, Principal p, string hospital, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hospital)) throw AppError.Invalid("Choose a hospital.");
        if (!p.HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");
        return await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
    }

    private static void Log(VeritaDbContext db, Guid hospitalId, string kind, string level, string message, string actor) =>
        db.HisSyncLogs.Add(new HisSyncLog { Id = Guid.NewGuid(), HospitalId = hospitalId, At = DateTime.UtcNow, Kind = kind, Level = level, Message = message, Actor = actor });

    private static HisSyncRangeDto Dto(HisSyncRange r) => new(r.Id, r.FromDate.ToString("yyyy-MM-dd"), r.ToDate.ToString("yyyy-MM-dd"), r.Status,
        r.DaysTotal, r.DaysDone, r.RequestedAt, r.RequestedBy, r.StartedAt, r.FinishedAt, r.Error, r.Mode);
}
