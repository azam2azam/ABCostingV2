using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Verita.Api.Infrastructure;
using Verita.Infrastructure.HisSync;

namespace Verita.Api.Controllers;

/// <summary>HIS Sync screen: settings, catalogue requests, fact date ranges, worker progress and the sync log.</summary>
[Route("api/v1/his-sync")]
public sealed class HisSyncController(HisSyncService sync) : VeritaController
{
    [HttpGet]
    [Authorize(Policy = "admin.view")]
    public async Task<HisSyncDto> Get([FromQuery] string hospital, [FromQuery] string? level, [FromQuery] int take = 200, CancellationToken ct = default) =>
        await sync.GetAsync(await Me(), hospital, level, take, ct);

    [HttpPut("settings")]
    [Authorize(Policy = "admin.manage")]
    public async Task<HisSyncSettingDto> SaveSettings([FromQuery] string hospital, [FromBody] HisSyncSettingsBody body, CancellationToken ct) =>
        await sync.SaveSettingsAsync(await Me(), hospital, body, ct);

    [HttpPost("catalogue")]
    [Authorize(Policy = "admin.manage")]
    public async Task<IActionResult> RequestCatalogue([FromQuery] string hospital, CancellationToken ct)
    {
        await sync.RequestCatalogueAsync(await Me(), hospital, ct);
        return NoContent();
    }

    [HttpPost("ranges")]
    [Authorize(Policy = "admin.manage")]
    public async Task<HisSyncRangeDto> AddRange([FromQuery] string hospital, [FromBody] HisSyncRangeBody body, CancellationToken ct) =>
        await sync.AddRangeAsync(await Me(), hospital, body, ct);

    /// <summary>Apply parked catalogue changes: their pack is queued on the import worker, as on Data Import.</summary>
    [HttpPost("catalogue-changes/{id:guid}/apply")]
    [Authorize(Policy = "admin.manage")]
    public async Task<IActionResult> ApplyCatalogue(Guid id, [FromQuery] string hospital, [FromServices] ImportQueue queue, CancellationToken ct)
    {
        var job = await sync.ApplyCatalogueAsync(await Me(), hospital, id, ct);
        await queue.EnqueueAsync(job);
        return NoContent();
    }

    [HttpPost("catalogue-changes/{id:guid}/discard")]
    [Authorize(Policy = "admin.manage")]
    public async Task<IActionResult> DiscardCatalogue(Guid id, [FromQuery] string hospital, CancellationToken ct)
    {
        await sync.DiscardCatalogueAsync(await Me(), hospital, id, ct);
        return NoContent();
    }

    [HttpDelete("ranges/{id:guid}")]
    [Authorize(Policy = "admin.manage")]
    public async Task<IActionResult> RemoveRange(Guid id, [FromQuery] string hospital, CancellationToken ct)
    {
        await sync.RemoveRangeAsync(await Me(), hospital, id, ct);
        return NoContent();
    }
}
