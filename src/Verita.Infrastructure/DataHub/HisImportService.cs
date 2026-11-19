using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.DataHub;

public sealed record ImportPreviewTable(string Entity, string FileName, int Rows);
public sealed record ImportPreviewHospital(string Code, string Name, string? HisSetupId, int? HisProjectId, bool Match, bool InScope);
public sealed record ImportPreview(
    bool HisPack, string FileName, string? SetupId, int? ProjectId, string? Pack, string? From, string? ToInclusive, string? ExtractedAtUtc,
    ImportPreviewTable[] Tables, string[] Skipped, ImportPreviewHospital[] Hospitals, string? SuggestedHospital,
    string[] Notices, string[] Warnings);

/// <summary>A pack read and mapped onto its hospital, ready to write: the tables in load order with their rows.</summary>
public sealed record PreparedImport(string FileName, List<(EntitySpec Spec, List<Dictionary<string, string>> Rows)> Set, string Source,
    string? Hospital, HisImportPack.Identity? Identity, bool FactsPack);

public sealed class HisImportService(IDbContextFactory<VeritaDbContext> factory, DataHubService hub, IAuditWriter audit, ILogger<HisImportService> log)
{
    public async Task<ImportPreview> PreviewAsync(Principal p, string fileName, Stream stream, string? entityHint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (files, identity, skipped) = ReadWithoutCostingModels(fileName, stream, entityHint);
        var unknown = files.Where(f => f.Spec is null).Select(f => f.FileName).ToArray();
        if (unknown.Length > 0)
            throw AppError.Invalid($"These files do not match a table: {string.Join(", ", unknown)}. Name each file after its table, as in the templates.");

        var notices = new List<string>();
        var warnings = new List<string>();
        if (skipped.Length > 0)
            notices.Add($"Skipped {string.Join(", ", skipped)}.");
        if (HisImportPack.IsFactsPack(identity, files))
        {
            files = HisImportPack.WithoutHisInventedActivityTransactions(files, out var droppedTxn);
            if (droppedTxn > 0)
                notices.Add($"Skipped {droppedTxn:N0} ActivityTransaction row(s). HIS packs use StaffTime, EncounterTransfer and ConsumptionItem — not invented ACT/RES volumes.");
        }
        var tables = files.Select(f => new ImportPreviewTable(f.Spec!.Name, f.FileName, f.Rows.Count)).ToArray();

        int? project = null;
        if (identity is not null && HisImportPack.TryProjectId(identity.ProjectId, out var pid)) project = pid;
        else if (identity is not null)
            warnings.Add($"ProjectID '{identity.ProjectId}' is not a number.");

        if (identity is not null)
        {
            var conflicts = HisImportPack.TenantConflicts(files, identity).Distinct().Take(5).ToList();
            if (conflicts.Count > 0)
                throw AppError.Invalid("This pack mixes HIS tenants. Extract one SetupID/ProjectID at a time.", new { conflicts });
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var hospitals = await db.Hospitals.AsNoTracking().OrderBy(h => h.Code).ToListAsync(ct);
        var listed = hospitals.Select(h =>
        {
            var inScope = p.HasHospital(h.Code);
            var match = identity is not null && project is not null
                && string.Equals(h.HisSetupId, identity.SetupId, StringComparison.OrdinalIgnoreCase)
                && h.HisProjectId == project;
            return new ImportPreviewHospital(h.Code, h.Name, h.HisSetupId, h.HisProjectId, match, inScope);
        }).Where(h => h.InScope).ToArray();

        string? suggested = null;
        if (identity is not null)
        {
            var matches = listed.Where(h => h.Match).Select(h => h.Code).ToArray();
            if (matches.Length == 1) suggested = matches[0];
            else if (matches.Length > 1)
                warnings.Add($"HIS SetupID {identity.SetupId} / ProjectID {identity.ProjectId} maps to {matches.Length} hospitals ({string.Join(", ", matches)}). Pick which VERITA hospital to load.");
            else
                warnings.Add($"No hospital has HIS SetupID {identity.SetupId} / ProjectID {identity.ProjectId} yet. Pick one to bind, or set the fields on Data entry first.");
            notices.Add($"HIS identity {identity.SetupId} / {identity.ProjectId}. Row codes use {HisImportPack.HisToken(identity.SetupId, identity.ProjectId)}; import remaps them to the hospital you choose.");
            if (tables.Any(t => t.Entity == "Hospital"))
                notices.Add("Hospital.csv is not applied. The VERITA hospital you pick keeps its name, city, currency and beds; only HIS SetupID/ProjectID are bound.");
            if (HisImportPack.IsFactsPack(identity, files) && HisImportPack.TryFactRange(identity, fileName, out var from, out var to))
                notices.Add($"Facts month {from:yyyy-MM}{(from.Month == to.Month && from.Year == to.Year ? "" : $" .. {to:yyyy-MM}")}: import adds the OPEN period. Create a calculation run yourself when you are ready.");
        }

        log.LogInformation("Import preview {File} his={His} setup={Setup} project={Project} tables={Tables} matches={Matches} suggested={Suggested} {Ms}ms",
            fileName, identity is not null, identity?.SetupId, project, tables.Length, listed.Count(h => h.Match), suggested, sw.ElapsedMilliseconds);

        return new ImportPreview(
            identity is not null, Path.GetFileName(fileName), identity?.SetupId, project,
            identity?.Pack, identity?.From, identity?.ToInclusive, identity?.ExtractedAtUtc,
            tables, skipped, listed, suggested, notices.ToArray(), warnings.ToArray());
    }

    public async Task<ImportResult[]> ImportAsync(Principal p, string fileName, Stream stream, string? hospital, bool dryRun, string? entityHint, CancellationToken ct,
        Func<string, int, int, Task>? onTable = null)
    {
        var sw = Stopwatch.StartNew();
        var prepared = await PrepareAsync(p, fileName, stream, hospital, dryRun, entityHint, ct);
        if (prepared.Set.Count == 0) return [];
        var (set, source, identity) = (prepared.Set, prepared.Source, prepared.Identity);
        hospital = prepared.Hospital;

        var results = await hub.ImportSetAsync(set, p, dryRun, source, ct, hospital, onTable);
        await FinishAsync(p, prepared, dryRun, results, sw.ElapsedMilliseconds, ct);
        return results;
    }

    /// <summary>
    /// Everything an import does before writing: read the pack, check its HIS tenant against the hospital (binding it on
    /// a first real import), map codes onto the hospital and drop what is not loaded. The HIS sync worker uses this to
    /// apply a large first catalogue table by table, in small committed batches, through
    /// <see cref="DataHubService.ImportSetAsync"/>.
    /// </summary>
    public async Task<PreparedImport> PrepareAsync(Principal p, string fileName, Stream stream, string? hospital, bool dryRun, string? entityHint, CancellationToken ct)
    {
        var (files, identity, skipped) = ReadWithoutCostingModels(fileName, stream, entityHint);
        var unknown = files.Where(f => f.Spec is null).Select(f => f.FileName).ToArray();
        if (unknown.Length > 0)
            throw AppError.Invalid($"These files do not match a table: {string.Join(", ", unknown)}. Name each file after its table, as in the templates.");
        if (files.Count == 0)
        {
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw AppError.Invalid("Name the file after its table (for example Resource.csv), or upload a HIS pack zip.");
            throw AppError.Invalid("The file has a header but no data rows.");
        }

        if (HisImportPack.IsFactsPack(identity, files))
        {
            files = HisImportPack.WithoutHisInventedActivityTransactions(files, out var droppedTxn);
            if (droppedTxn > 0)
                log.LogInformation("Import {File}: skipped {Count} ActivityTransaction row(s) (HIS packs use StaffTime / EncounterTransfer / ConsumptionItem)", fileName, droppedTxn);
        }

        if (identity is not null)
        {
            if (string.IsNullOrWhiteSpace(hospital))
                throw AppError.Invalid($"This is a HIS pack (SetupID {identity.SetupId} / ProjectID {identity.ProjectId}). Preview it, choose the VERITA hospital, then proceed.");
            if (!HisImportPack.TryProjectId(identity.ProjectId, out var projectId))
                throw AppError.Invalid($"ProjectID '{identity.ProjectId}' is not a number.");
            var conflicts = HisImportPack.TenantConflicts(files, identity).Distinct().Take(5).ToList();
            if (conflicts.Count > 0)
                throw AppError.Invalid("This pack mixes HIS tenants.", new { conflicts });

            await using var db = await factory.CreateDbContextAsync(ct);
            var h = await db.Hospitals.FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
            if (!p.HasHospital(h.Code)) throw AppError.OutOfScope($"Hospital {h.Code}");
            if (!string.IsNullOrEmpty(h.HisSetupId) || h.HisProjectId is not null)
            {
                if (!string.Equals(h.HisSetupId, identity.SetupId, StringComparison.OrdinalIgnoreCase) || h.HisProjectId != projectId)
                    throw AppError.Invalid($"{h.Code} is bound to HIS {h.HisSetupId}/{h.HisProjectId}, not {identity.SetupId}/{identity.ProjectId}.");
            }
            else if (!dryRun)
            {
                h.HisSetupId = identity.SetupId;
                h.HisProjectId = projectId;
                await db.SaveChangesAsync(ct);
                log.LogInformation("Bound hospital {Hospital} to HIS {Setup}/{Project}", h.Code, identity.SetupId, projectId);
            }

            foreach (var file in files)
            {
                foreach (var row in file.Rows)
                    HisImportPack.BindHospital(row, file.Spec!.Name, h.Code, identity.SetupId, identity.ProjectId,
                        h.Name, h.City, h.Currency, h.IsActive);
            }
            files = files.Where(f => f.Spec!.Name != "Hospital").ToList();
            hospital = h.Code;
            (files, var unbilled) = HisImportPack.SkipUnbilledEncounters(files);
            if (unbilled > 0) log.LogInformation("Import {File}: skipped {Count} encounters with nothing billed", fileName, unbilled);
            var mentioned = files.Where(f => f.Spec!.Name is "Encounter" or "EncounterService" or "StaffTime")
                .SelectMany(f => f.Rows).Select(r => r.GetValueOrDefault("providerCode")?.Trim())
                .Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = (mentioned.Count == 0
                    ? []
                    : await db.Providers.AsNoTracking().Where(x => mentioned.Contains(x.Code)).Select(x => x.Code).ToListAsync(ct))
                .Concat(files.Where(f => f.Spec!.Name == "Provider").SelectMany(f => f.Rows).Select(r => r.GetValueOrDefault("code") ?? ""))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (HisImportPack.UnknownDoctors(files, known) is { Count: > 0 } doctors)
            {
                log.LogWarning("Import {File}: {Count} doctors are not in the catalogue; loading them as Not assigned", fileName, doctors.Count);
                files = [.. files, new HisImportPack.ParsedFile("Unknown doctors", EntitySpecs.Find("Provider"), doctors)];
                files = files.OrderBy(f => Array.IndexOf(EntitySpecs.All, f.Spec)).ToList();
            }
            if (files.Count == 0)
            {
                log.LogInformation("Import {Mode} {File} hospital={Hospital} skipped Hospital.csv (VERITA identity kept)",
                    dryRun ? "validate" : "commit", fileName, hospital);
                return new PreparedImport(fileName, [], identity is null ? "UPLOAD" : $"HIS-{identity.SetupId}-{identity.ProjectId}", hospital, identity, false);
            }
        }

        foreach (var file in files)
        {
            var spec = file.Spec!;
            var unknownCols = file.Rows[0].Keys.Where(k => !HisImportPack.IsKnownColumn(spec, k)).ToList();
            if (unknownCols.Count > 0)
                throw AppError.Invalid($"Columns not in the {spec.Name} template: {string.Join(", ", unknownCols)}.");
        }

        var set = files.Select(f => (f.Spec!, f.Rows.Select(HisImportPack.StripExtra).ToList())).ToList();
        var source = identity is null ? "UPLOAD" : $"HIS-{identity.SetupId}-{identity.ProjectId}";
        log.LogInformation("Import {Mode} {File} hospital={Hospital} setup={Setup} project={Project} tables={Tables} skipped={Skipped}",
            dryRun ? "validate" : "commit", fileName, hospital, identity?.SetupId, identity?.ProjectId, set.Count, skipped.Length);

        return new PreparedImport(fileName, set, source, hospital, identity, HisImportPack.IsFactsPack(identity, files));
    }

    /// <summary>After an import: log, audit, and for facts make sure their months exist as open periods.</summary>
    public async Task FinishAsync(Principal p, PreparedImport prepared, bool dryRun, ImportResult[] results, long elapsedMs, CancellationToken ct)
    {
        var (fileName, hospital, identity) = (prepared.FileName, prepared.Hospital, prepared.Identity);
        var rejected = results.Sum(r => r.Rejected);
        log.LogInformation("Import finished {Mode} {File} hospital={Hospital} tables={Tables} rejected={Rejected} {Ms}ms",
            dryRun ? "validate" : "commit", fileName, hospital, results.Length, rejected, elapsedMs);

        if (!dryRun)
        {
            await audit.WriteAsync(p, "CREATE", "ImportBatch", null, identity is null ? fileName : $"HIS {identity.SetupId}/{identity.ProjectId} → {hospital}",
                metadata: new { file = fileName, hospital, setupId = identity?.SetupId, projectId = identity?.ProjectId, dryRun, tables = results.Select(r => r.Entity).ToArray(), rejected },
                ct: ct);
            if (prepared.FactsPack && !string.IsNullOrWhiteSpace(hospital))
                await EnsureFactPeriodAsync(hospital, identity, fileName, ct);
        }
    }

    private static (List<HisImportPack.ParsedFile> Files, HisImportPack.Identity? His, string[] Skipped) ReadWithoutCostingModels(string fileName, Stream stream, string? entity)
    {
        var (files, identity, skipped) = HisImportPack.Read(fileName, stream, entity);
        var blocked = files.Where(f => f.Spec is not null && HisImportPack.CostingModelTables.Contains(f.Spec.Name)).ToList();
        if (blocked.Count == 0) return (files, identity, skipped);
        if (blocked.Count == files.Count)
            throw AppError.Invalid($"Import does not change costing models, so {string.Join(", ", blocked.Select(f => f.Spec!.Name).Distinct())} cannot be uploaded. Use Costing Models or Data entry.");
        return (files.Except(blocked).ToList(), identity, [.. skipped, .. blocked.Select(f => f.FileName)]);
    }

    private async Task EnsureFactPeriodAsync(string hospitalCode, HisImportPack.Identity? identity, string fileName, CancellationToken ct)
    {
        if (!HisImportPack.TryFactRange(identity, fileName, out var from, out var to)) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var hospital = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(h => h.Code == hospitalCode, ct);
        if (hospital is null) return;
        foreach (var code in await OpenPeriods.EnsureHospitalMonthsAsync(db, hospital.Id, from, to, ct))
            log.LogInformation("Facts import created period {Period} for {Hospital}", code, hospitalCode);
    }
}
