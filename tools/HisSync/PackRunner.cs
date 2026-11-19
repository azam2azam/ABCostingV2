using HisExtract;
using Verita.Infrastructure.DataHub;

namespace Verita.HisSync;

/// <summary>An extracted pack: the zip's bytes, its name, and the steps that failed and were skipped.</summary>
public sealed record Pack(string FileName, byte[] Bytes, IReadOnlyList<string> Warnings);

/// <summary>
/// Runs one HisExtract step into a pack and imports it through the same <see cref="HisImportService"/> as Data
/// Import, so a synced row passes exactly the checks an uploaded one does.
/// </summary>
public sealed class PackRunner(IServiceScopeFactory scopes, SyncStore store)
{
    public async Task<Pack> ExtractAsync(Settings s, string fileName, string readme, Func<CsvZipWriter, Task> extract, CancellationToken ct)
    {
        var dir = Path.Combine(store.Options.WorkDir, Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(dir, fileName);
        try
        {
            IReadOnlyList<string> warnings;
            using (var pack = new CsvZipWriter(zip, readme, s))
            {
                try
                {
                    await extract(pack);
                }
                catch (Exception ex)
                {
                    pack.Fail(ex);
                    throw;
                }
                ct.ThrowIfCancellationRequested();
                pack.Completed = true;
                warnings = pack.Warnings.ToList();
            }
            return new Pack(fileName, await File.ReadAllBytesAsync(zip, ct), warnings);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    public async Task<ImportResult[]> ImportAsync(Pack pack, string hospital, bool dryRun, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var his = scope.ServiceProvider.GetRequiredService<HisImportService>();
        return await his.ImportAsync(SyncStore.System, pack.FileName, new MemoryStream(pack.Bytes, writable: false), hospital, dryRun, null, ct);
    }

    /// <summary>Keep a pack as an import file (Data Import's store), for a person to apply later.</summary>
    public async Task<Guid> KeepAsync(Pack pack, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ImportFileService>();
        var detail = await files.UploadAsync(SyncStore.System, pack.FileName, new MemoryStream(pack.Bytes, writable: false), ct);
        return detail.File.Id;
    }

    public async Task DeleteKeptAsync(Guid fileId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ImportFileService>();
        await files.DeleteAsync(SyncStore.System, fileId, ct);
    }

    public static string Readme(Settings s, string kind, string detail) => $"""
        HIS sync ({kind})
        HIS SetupID: {s.SetupId}
        HIS ProjectID: {s.ProjectId}
        {detail}
        Extracted (UTC): {DateTime.UtcNow:u}
        Written by the VERITA HIS sync worker and imported straight away; not kept.
        """;
}
