using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Verita.Api.Infrastructure;
using Verita.Application;
using Verita.Domain;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Governance;
using Verita.Infrastructure.Queries;
using Verita.Infrastructure.Runs;

namespace Verita.Api.Controllers;

[Route("api/v1")]
public sealed class RunsController(RunService runs, RunQueue queue, VeritaDbContext db) : VeritaController
{
    /// <summary>Hospitals, periods and runs in the caller's scope — the context bar (FR-11.2).</summary>
    [HttpGet("context")]
    [Authorize(Policy = "dashboard.view")]
    public async Task<ReadQueries.ContextDto> Context(CancellationToken ct) => await Q.ContextAsync(await Me(), ct);

    [HttpGet("runs")]
    [Authorize(Policy = "costing.view")]
    public async Task<ReadQueries.RunListItem[]> List(CancellationToken ct) => await Q.RunsAsync(await Me(), ct);

    [HttpGet("runs/{run}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<ReadQueries.RunDetail>> Detail(string run, CancellationToken ct)
    {
        var p = await Me();
        var s = await Q.Scope(p, run, null, null, ct);
        return Wrap(s, await Q.RunDetailAsync(s, p, (d, r) => runs.PublishBlockersAsync(d, r, ct), ct));
    }

    public sealed record CreateRunRequest(string Hospital, string Period, string? Name);

    [HttpPost("runs")]
    [Authorize(Policy = "run.execute")]
    public async Task<ActionResult<ReadQueries.RunListItem>> Create(CreateRunRequest req, CancellationToken ct)
    {
        var p = await Me();
        if (!p.HasHospital(req.Hospital)) throw AppError.OutOfScope($"Hospital {req.Hospital}");
        var hospital = await db.Hospitals.FirstOrDefaultAsync(h => h.Code == req.Hospital, ct) ?? throw AppError.NotFound($"Hospital {req.Hospital}");
        var period = await db.Periods.FirstOrDefaultAsync(x => x.Code == req.Period, ct) ?? throw AppError.NotFound($"Period {req.Period}");
        var run = await runs.CreateAsync(hospital.Id, period.Id, p.Email, req.Name, ct: ct);
        return Created($"/api/v1/runs/{run.Id}", (await Q.RunsAsync(p, ct)).First(r => r.Id == run.Id));
    }

    public sealed record ExecuteRequest(bool Force = false);

    /// <summary>ARCH-8: queued, not executed in the request. Poll GET runs/{id} for its stages.</summary>
    [HttpPost("runs/{run}/execute")]
    [Authorize(Policy = "run.execute")]
    public async Task<IActionResult> Execute(string run, ExecuteRequest req, CancellationToken ct)
    {
        var p = await Me();
        var s = await Q.Scope(p, run, null, null, ct);
        if (Catalogue.IsRunImmutable(s.Run.Status)) throw AppError.RunImmutable(s.Run.Code);
        await queue.EnqueueAsync(new RunJob(s.Run.Id, req.Force, p.Email));
        return Accepted($"/api/v1/runs/{s.Run.Id}", new { runId = s.Run.Id, status = "QUEUED", poll = $"/api/v1/runs/{s.Run.Id}" });
    }

    public sealed record TransitionRequest(string To);

    [HttpPost("runs/{run}/transition")]
    [Authorize(Policy = "costing.view")]
    public async Task<IActionResult> Transition(string run, TransitionRequest req, CancellationToken ct)
    {
        var p = await Me();
        var s = await Q.Scope(p, run, null, null, ct);
        var updated = await runs.TransitionAsync(s.Run.Id, req.To, p, ct);
        return Ok(new { runId = updated.Id, status = updated.Status });
    }
}

[Route("api/v1")]
public sealed class GovernanceController(GovernanceService governance) : VeritaController
{
    [HttpGet("governance")]
    [Authorize(Policy = "governance.view")]
    public async Task<ReadQueries.GovernanceDto> Get(CancellationToken ct) => await Q.GovernanceAsync(ct);

    public sealed record ApprovalRequestBody(string EntityType, Guid EntityId, string Note);

    [HttpPost("approvals")]
    [Authorize(Policy = "approval.request")]
    public async Task<IActionResult> Request(ApprovalRequestBody body, CancellationToken ct)
    {
        var p = await Me();
        if (body.EntityType == "CALCULATION_RUN") await Q.Scope(p, body.EntityId.ToString(), null, null, ct);
        var req = await governance.RequestAsync(body.EntityType, body.EntityId, body.Note, p, ct);
        return Ok(new { req.Id, req.Stage });
    }

    public sealed record DecisionBody(bool Approve, string? Note);

    /// <summary>FR-0.1: the service refuses self-approval whatever the caller sends.</summary>
    [HttpPost("approvals/{id:guid}/decide")]
    [Authorize(Policy = "governance.view")]
    public async Task<IActionResult> Decide(Guid id, DecisionBody body, CancellationToken ct)
    {
        var req = await governance.DecideAsync(id, body.Approve, body.Note, await Me(), ct);
        return Ok(new { req.Id, req.Stage });
    }
}
