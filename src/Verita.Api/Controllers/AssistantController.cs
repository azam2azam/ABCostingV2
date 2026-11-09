using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Verita.Infrastructure.Assistant;

namespace Verita.Api.Controllers;

/// <summary>
/// The rule-based assistant (no model, no inference): it matches a question to an intent, reads
/// the answer through the same queries as the screens, and keeps each user's threads. Every role
/// holds help.view; each answer then checks the permission its own data needs.
/// </summary>
[Route("api/v1/assistant")]
[Authorize(Policy = "help.view")]
public sealed class AssistantController(AssistantService assistant) : VeritaController
{
    [HttpGet("threads")]
    public async Task<AssistantThreadDto[]> Threads([FromQuery] bool archived = false, CancellationToken ct = default) =>
        await assistant.ThreadsAsync(await Me(), archived, ct);

    [HttpGet("threads/{id:guid}")]
    public async Task<AssistantThreadDetail> Thread(Guid id, CancellationToken ct) => await assistant.ThreadAsync(await Me(), id, ct);

    [HttpPatch("threads/{id:guid}")]
    public async Task<AssistantThreadDto> Patch(Guid id, [FromBody] ThreadPatch body, CancellationToken ct) => await assistant.PatchAsync(await Me(), id, body, ct);

    [HttpDelete("threads/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await assistant.DeleteAsync(await Me(), id, ct);
        return NoContent();
    }

    [HttpPost("ask")]
    public async Task<AskResult> Ask([FromBody] AskBody body, CancellationToken ct) => await assistant.AskAsync(await Me(), body, ct);

    [HttpPost("messages/{id:guid}/feedback")]
    public async Task<AssistantMessageDto> Feedback(Guid id, [FromBody] FeedbackBody body, CancellationToken ct) =>
        await assistant.FeedbackAsync(await Me(), id, body, ct);

    [HttpPost("messages/{id:guid}/refresh")]
    public async Task<AskResult> Refresh(Guid id, CancellationToken ct) => await assistant.RefreshAsync(await Me(), id, ct);
}
