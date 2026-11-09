using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Verita.Application;
using Verita.Infrastructure.CostingSeed;
using Verita.Infrastructure.DataHub;

namespace Verita.Api.Controllers;

/// <summary>
/// Data entry and template uploads (BRD 22.2; the master-data editing the BRD places in phase 2).
/// Every table in <see cref="EntitySpecs"/> gets a template, an upload, a list and a form.
/// </summary>
[Route("api/v1/data")]
public sealed class DataController(DataHubService hub, RecipeSuggestionService recipes, HisImportService his, ILogger<DataController> log) : VeritaController
{
    private static EntitySpec Spec(string entity) => EntitySpecs.Find(entity) ?? throw AppError.NotFound($"Table {entity}");

    /// <summary>Computed recipe hints for Data entry. Not stored — Save on the form writes the row.</summary>
    [HttpGet("recipe-suggestions")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<RecipeSuggestionList> RecipeSuggestions([FromQuery] string hospital, [FromQuery] string? entity, CancellationToken ct) =>
        await recipes.ListAsync(await Me(), hospital, entity, ct);

    [HttpGet("entities")]
    [Authorize(Policy = "masterdata.view")]
    public EntityDescription[] Entities() => EntitySpecs.All.Select(DataHubService.Describe).ToArray();

    [HttpGet("{entity}")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<RowPage> List(string entity, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default) =>
        await hub.ListAsync(Spec(entity), await Me(), search, page, pageSize, ct);

    [HttpGet("{entity}/lookup")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Dictionary<string, string>[]> Lookup(string entity, CancellationToken ct) => await hub.LookupAsync(Spec(entity), await Me(), ct);

    /// <summary>Save one row from the data-entry form: created or updated on its natural key.</summary>
    [HttpPost("{entity}")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<ImportResult> Save(string entity, [FromBody] Dictionary<string, string?> row, CancellationToken ct)
    {
        var values = row.ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);
        values["__row"] = "1";
        var result = await hub.SaveAsync(Spec(entity), await Me(), [values], dryRun: false, "DATA-ENTRY", ct);
        if (result.Rejected > 0) throw AppError.Invalid("The row was not saved.", new { errors = result.Rejects[0].Errors });
        Verita.Infrastructure.Assistant.AssistantVocabulary.Invalidate();
        return result;
    }

    [HttpGet("{entity}/template")]
    [Authorize(Policy = "masterdata.view")]
    public IActionResult Template(string entity)
    {
        var spec = Spec(entity);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(DataHubService.Template(spec))).ToArray(), "text/csv", $"{spec.Name}.csv");
    }

    /// <summary>Every template in load order, numbered, in one zip — the onboarding pack.</summary>
    [HttpGet("templates.zip")]
    [Authorize(Policy = "masterdata.view")]
    public IActionResult Templates()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var i = 1;
            foreach (var spec in EntitySpecs.All)
            {
                var entry = zip.CreateEntry($"{i++:00}_{spec.Name}.csv");
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                w.Write(DataHubService.Template(spec));
            }
            var readme = zip.CreateEntry("README.txt");
            using var r = new StreamWriter(readme.Open());
            r.Write("VERITA onboarding templates.\r\n\r\nFill the files in number order: each table refers to tables before it by code.\r\n" +
                    "Keep the header row. The second row (starting with #) is guidance and is skipped on upload.\r\n" +
                    "Save as CSV UTF-8. Dates yyyy-mm-dd, numbers with a dot and no thousands separators.\r\n" +
                    "Upload one file at a time on the Data Import screen, or zip the filled files and upload the zip: it is processed in number order.\r\n" +
                    "Leave a file empty (header only) if you have nothing for it.\r\n");
        }
        return File(ms.ToArray(), "application/zip", "verita-templates.zip");
    }

    /// <summary>Parse a HIS or VERITA pack: tenant, row counts, matching hospitals. Saves nothing.</summary>
    [HttpPost("import/preview")]
    [Authorize(Policy = "masterdata.view")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ImportPreview> ImportPreview([FromForm] string? entity, IFormFile file, CancellationToken ct)
    {
        var p = await Me();
        if (file is null || file.Length == 0) throw AppError.Invalid("Choose a file to upload.");
        log.LogInformation("Import preview {File} {Bytes}b user={Email}", file.FileName, file.Length, p.Email);
        await using var stream = file.OpenReadStream();
        return await his.PreviewAsync(p, file.FileName, stream, entity, ct);
    }

    /// <summary>Upload a filled template (CSV), or a zip of them processed in load order. dryRun validates without saving. HIS packs require hospital.</summary>
    [HttpPost("import")]
    [Authorize(Policy = "masterdata.view")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ImportResult[]> Import([FromForm] string? entity, IFormFile file, [FromForm] bool dryRun = false, [FromForm] string? hospital = null, CancellationToken ct = default)
    {
        var p = await Me();
        if (file is null || file.Length == 0) throw AppError.Invalid("Choose a file to upload.");
        log.LogInformation("Import {Mode} {File} {Bytes}b hospital={Hospital} user={Email}",
            dryRun ? "validate" : "commit", file.FileName, file.Length, hospital, p.Email);
        await using var stream = file.OpenReadStream();
        var results = await his.ImportAsync(p, file.FileName, stream, hospital, dryRun, entity, ct);
        // New procedures and packages should be recognisable by the assistant at once.
        if (!dryRun) Verita.Infrastructure.Assistant.AssistantVocabulary.Invalidate();
        return results;
    }

    [HttpGet("imports")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<ImportBatchDto[]> History(CancellationToken ct) => await hub.HistoryAsync(ct);

    [HttpGet("imports/{id:guid}/rejects")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<IActionResult> Rejects(Guid id, CancellationToken ct)
    {
        var (entity, csv) = await hub.RejectsCsvAsync(id, ct);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", $"{entity}-rejects.csv");
    }
}
