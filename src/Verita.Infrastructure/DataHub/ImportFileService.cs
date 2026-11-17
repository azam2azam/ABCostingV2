using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.DataHub;

public sealed record ImportFileDto(
    Guid Id, string FileName, long SizeBytes, DateTime UploadedAt, string UploadedBy,
    bool IsHisPack, string? HisSetupId, int? HisProjectId, string? Pack, string? FactsFrom, string? FactsTo,
    string? Hospital, int TotalRows, string Status, bool LastDryRun, int TablesTotal, int TablesDone, string? CurrentTable,
    int Attempts, DateTime? QueuedAt, DateTime? StartedAt, DateTime? FinishedAt, string? RunBy,
    int RowsCreated, int RowsUpdated, int RowsUnchanged, int RowsRejected, string? ErrorMessage);

public sealed record ImportFileDetail(ImportFileDto File, ImportPreview? Preview, ImportResult[] Results);

/// <summary>A queued validate / import of a stored file, run by the background import worker.</summary>
public sealed record ImportFileJob(Guid FileId, string? Hospital, bool DryRun, Principal Actor);

/// <summary>
/// Uploaded import files: stored at upload (with the preview), validated or imported later in the
/// background, and kept so a failed or partial import can be run again from the list.
/// </summary>
public sealed class ImportFileService(IDbContextFactory<VeritaDbContext> factory, HisImportService his, IAuditWriter audit, ILogger<ImportFileService> log)
{
    public static readonly string[] Active = ["QUEUED", "RUNNING"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Store the file and its preview. Rejected (nothing stored) if it does not read as a template or HIS pack.</summary>
    public async Task<ImportFileDetail> UploadAsync(Principal p, string fileName, Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) throw AppError.Invalid("The file is empty.");

        var preview = await his.PreviewAsync(p, fileName, new MemoryStream(bytes, writable: false), null, ct);
        if (preview.Tables.Length == 0) throw AppError.Invalid("The file has no tables to import.");

        var file = new ImportFile
        {
            Id = IdGen.Sequential(), FileName = Path.GetFileName(fileName), SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            UploadedAt = DateTime.UtcNow, UploadedBy = p.Email,
            IsHisPack = preview.HisPack, HisSetupId = preview.SetupId, HisProjectId = preview.ProjectId, Pack = preview.Pack,
            FactsFrom = preview.From, FactsTo = preview.ToInclusive, Hospital = preview.SuggestedHospital,
            TotalRows = preview.Tables.Sum(t => t.Rows), TablesTotal = preview.Tables.Length,
            ContextJson = JsonSerializer.Serialize(preview, Json), Status = "UPLOADED",
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ImportFiles.Add(file);
        db.ImportFileContents.Add(new ImportFileContent { Id = file.Id, Content = bytes });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import file {Id} {File} {Bytes}b uploaded by {Email}", file.Id, file.FileName, bytes.Length, p.Email);
        await audit.WriteAsync(p, "CREATE", "ImportFile", file.Id.ToString(), file.FileName,
            metadata: new { file.SizeBytes, file.IsHisPack, file.HisSetupId, file.HisProjectId, file.TotalRows }, ct: ct);
        return new ImportFileDetail(Dto(file), preview, []);
    }

    public async Task<ImportFileDto[]> ListAsync(Principal p, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var files = await db.ImportFiles.AsNoTracking().OrderByDescending(f => f.UploadedAt).Take(200).ToListAsync(ct);
        return files.Where(f => Visible(p, f)).Select(Dto).ToArray();
    }

    public async Task<ImportFileDetail> GetAsync(Principal p, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var f = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null || !Visible(p, f)) throw AppError.NotFound("Import file");
        return new ImportFileDetail(Dto(f), Read<ImportPreview>(f.ContextJson), Read<ImportResult[]>(f.DetailJson) ?? []);
    }

    /// <summary>Mark the file QUEUED and return the job for the worker. A HIS pack needs a hospital.</summary>
    public async Task<ImportFileJob> QueueAsync(Principal p, Guid id, string? hospital, bool dryRun, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var f = await db.ImportFiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null || !Visible(p, f)) throw AppError.NotFound("Import file");
        if (Active.Contains(f.Status)) throw AppError.Conflict("IMPORT_RUNNING", $"{f.FileName} is already {f.Status.ToLowerInvariant()}.");
        hospital = string.IsNullOrWhiteSpace(hospital) ? f.Hospital : hospital.Trim();
        if (f.IsHisPack && string.IsNullOrWhiteSpace(hospital)) throw AppError.Invalid("Select the hospital at the top of the screen.");
        if (!string.IsNullOrWhiteSpace(hospital) && !p.HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");
        if (f.IsHisPack)
        {
            // A HIS pack loads only into the hospital bound to the same HIS SetupID / ProjectID.
            var h = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
            if (MismatchMessage(f, h.Code, h.HisSetupId, h.HisProjectId) is { } mismatch) throw AppError.Invalid(mismatch);
        }
        // Several files may be queued: the single import worker runs them one at a time, in the order queued.

        f.Hospital = string.IsNullOrWhiteSpace(hospital) ? null : hospital;
        f.Status = "QUEUED";
        f.LastDryRun = dryRun;
        f.QueuedAt = DateTime.UtcNow;
        f.StartedAt = null;
        f.FinishedAt = null;
        f.TablesDone = 0;
        f.CurrentTable = null;
        f.ErrorMessage = null;
        f.RunBy = p.Email;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import file {Id} {File} queued {Mode} hospital={Hospital} by {Email}", f.Id, f.FileName, dryRun ? "validate" : "import", f.Hospital, p.Email);
        return new ImportFileJob(f.Id, f.Hospital, dryRun, p);
    }

    public async Task DeleteAsync(Principal p, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var f = await db.ImportFiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null || !Visible(p, f)) throw AppError.NotFound("Import file");
        if (Active.Contains(f.Status)) throw AppError.Conflict("IMPORT_RUNNING", $"{f.FileName} is {f.Status.ToLowerInvariant()} and cannot be removed.");
        await db.ImportFileContents.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        db.ImportFiles.Remove(f);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "DELETE", "ImportFile", id.ToString(), f.FileName, ct: ct);
    }

    /// <summary>The background part: read the stored bytes, import them, and record progress and the outcome.</summary>
    public async Task RunAsync(ImportFileJob job, CancellationToken ct)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var n = await db.ImportFiles.Where(x => x.Id == job.FileId && x.Status == "QUEUED")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "RUNNING").SetProperty(x => x.StartedAt, DateTime.UtcNow)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (n == 0) return; // removed or no longer queued
        }

        string fileName;
        byte[] bytes;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            fileName = await db.ImportFiles.Where(x => x.Id == job.FileId).Select(x => x.FileName).FirstAsync(ct);
            bytes = await db.ImportFileContents.Where(x => x.Id == job.FileId).Select(x => x.Content).FirstAsync(ct);
        }

        try
        {
            var results = await his.ImportAsync(job.Actor, fileName, new MemoryStream(bytes, writable: false), job.Hospital, job.DryRun, null, ct,
                onTable: async (table, index, total) =>
                {
                    await using var pdb = await factory.CreateDbContextAsync(CancellationToken.None);
                    await pdb.ImportFiles.Where(x => x.Id == job.FileId)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentTable, table).SetProperty(x => x.TablesDone, index).SetProperty(x => x.TablesTotal, total), CancellationToken.None);
                });
            if (!job.DryRun) Assistant.AssistantVocabulary.Invalidate();
            var rejected = results.Sum(r => r.Rejected);
            var status = job.DryRun ? "VALIDATED" : rejected > 0 ? "COMPLETED_WITH_REJECTS" : "COMPLETED";
            var detail = JsonSerializer.Serialize(results, Json);
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            await db.ImportFiles.Where(x => x.Id == job.FileId).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, status).SetProperty(x => x.FinishedAt, DateTime.UtcNow)
                .SetProperty(x => x.CurrentTable, (string?)null).SetProperty(x => x.TablesDone, x => x.TablesTotal)
                .SetProperty(x => x.RowsCreated, results.Sum(r => r.Created)).SetProperty(x => x.RowsUpdated, results.Sum(r => r.Updated))
                .SetProperty(x => x.RowsUnchanged, results.Sum(r => r.Unchanged)).SetProperty(x => x.RowsRejected, rejected)
                .SetProperty(x => x.DetailJson, detail).SetProperty(x => x.ErrorMessage, (string?)null), CancellationToken.None);
            log.LogInformation("Import file {Id} {File} {Status}: created={Created} updated={Updated} rejected={Rejected}",
                job.FileId, fileName, status, results.Sum(r => r.Created), results.Sum(r => r.Updated), rejected);
        }
        catch (Exception ex)
        {
            var message = ex is AppError ? ex.Message : ex is OperationCanceledException ? "Stopped before it finished. Import again." : $"Import failed: {Inner(ex)}";
            if (ex is not AppError and not OperationCanceledException) log.LogError(ex, "Import file {Id} {File} failed", job.FileId, fileName);
            else log.LogWarning("Import file {Id} {File} failed: {Message}", job.FileId, fileName, message);
            await MarkFailedAsync(job.FileId, message);
            if (ex is OperationCanceledException) throw;
        }
    }

    /// <summary>Jobs live in memory: anything QUEUED or RUNNING when the API starts was cut off and must be run again.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var n = await db.ImportFiles.Where(x => Active.Contains(x.Status)).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Status, "FAILED").SetProperty(x => x.FinishedAt, DateTime.UtcNow).SetProperty(x => x.CurrentTable, (string?)null)
            .SetProperty(x => x.ErrorMessage, "Stopped when the server restarted. Nothing after the last finished table was saved; import again."), ct);
        if (n > 0) log.LogWarning("Marked {Count} interrupted import file(s) as FAILED", n);
    }

    private async Task MarkFailedAsync(Guid id, string message)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        await db.ImportFiles.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Status, "FAILED").SetProperty(x => x.FinishedAt, DateTime.UtcNow)
            .SetProperty(x => x.CurrentTable, (string?)null).SetProperty(x => x.ErrorMessage, message), CancellationToken.None);
    }

    private static string Inner(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    /// <summary>Null when the HIS pack belongs to the hospital; otherwise why it cannot be imported there.</summary>
    public static string? MismatchMessage(ImportFile f, string hospital, string? hisSetupId, int? hisProjectId)
    {
        if (!f.IsHisPack) return null;
        var file = $"HIS SetupID {f.HisSetupId} / ProjectID {f.HisProjectId}";
        if (string.IsNullOrWhiteSpace(hisSetupId) || hisProjectId is null)
            return $"{f.FileName} is {file}, but {hospital} has no HIS SetupID / ProjectID. Set them on the hospital in Data Entry first.";
        if (!string.Equals(hisSetupId.Trim(), f.HisSetupId?.Trim(), StringComparison.OrdinalIgnoreCase) || hisProjectId != f.HisProjectId)
            return $"{f.FileName} is {file}, but {hospital} is HIS SetupID {hisSetupId} / ProjectID {hisProjectId}. Select the matching hospital at the top of the screen.";
        return null;
    }

    private static bool Visible(Principal p, ImportFile f) => string.IsNullOrWhiteSpace(f.Hospital) || p.HasHospital(f.Hospital) || f.UploadedBy == p.Email;

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); } catch (JsonException) { return null; }
    }

    private static ImportFileDto Dto(ImportFile f) => new(
        f.Id, f.FileName, f.SizeBytes, f.UploadedAt, f.UploadedBy, f.IsHisPack, f.HisSetupId, f.HisProjectId, f.Pack, f.FactsFrom, f.FactsTo,
        f.Hospital, f.TotalRows, f.Status, f.LastDryRun, f.TablesTotal, f.TablesDone, f.CurrentTable, f.Attempts, f.QueuedAt, f.StartedAt,
        f.FinishedAt, f.RunBy, f.RowsCreated, f.RowsUpdated, f.RowsUnchanged, f.RowsRejected, f.ErrorMessage);
}
