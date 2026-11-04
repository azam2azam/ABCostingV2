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
        var period = await db.Periods.FirstOrDefaultAsync(x => x.HospitalId == hospital.Id && x.Code == req.Period, ct) ?? throw AppError.NotFound($"Period {req.Period}");
        var run = await runs.CreateAsync(hospital.Id, period.Id, p.Email, req.Name, ct: ct);
        return Created($"/api/v1/runs/{run.Id}", (await Q.RunsAsync(p, ct)).First(r => r.Id == run.Id));
    }

    public sealed record AppliedVersion(string? Code, string? Name, string? Status);

    /// <summary>The costing model a new run for this period would be calculated under.</summary>
    [HttpGet("runs/applied-version")]
    [Authorize(Policy = "run.execute")]
    public async Task<AppliedVersion> Applied([FromQuery] string hospital, [FromQuery] string period, CancellationToken ct)
    {
        if (!(await Me()).HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");
        var owner = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
        var row = await db.Periods.AsNoTracking().FirstOrDefaultAsync(x => x.HospitalId == owner.Id && x.Code == period, ct) ?? throw AppError.NotFound($"Period {period}");
        var version = await RunService.AppliedVersionAsync(db, owner.Id, row, ct);
        return new AppliedVersion(version?.Code, version?.Name, version?.Status);
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
        if (s.Run.Status is "VALIDATING" or "VALIDATED" or "CALCULATING" or "CALCULATED")
            throw AppError.Conflict("RUN_IN_FLIGHT", $"{s.Run.Code} is already running.");
        await db.CalculationRuns.Where(r => r.Id == s.Run.Id).ExecuteUpdateAsync(x => x
            .SetProperty(r => r.Status, "VALIDATING")
            .SetProperty(r => r.FinishedAt, (DateTime?)null)
            .SetProperty(r => r.ErrorMessage, (string?)null), ct);
        await queue.EnqueueAsync(new RunJob(s.Run.Id, req.Force, p.Email));
        return Accepted($"/api/v1/runs/{s.Run.Id}", new { runId = s.Run.Id, status = "VALIDATING", poll = $"/api/v1/runs/{s.Run.Id}" });
    }

    /// <summary>Remove a DRAFT or FAILED run. Same permission as creating one; reconciled or published runs stay.</summary>
    [HttpDelete("runs/{run}")]
    [Authorize(Policy = "run.execute")]
    public async Task<IActionResult> Delete(string run, CancellationToken ct)
    {
        var p = await Me();
        var s = await Q.Scope(p, run, null, null, ct);
        await runs.DeleteDraftAsync(s.Run.Id, p, ct);
        return NoContent();
    }

    public sealed record NewPeriod(string Hospital, string? Code, DateOnly StartDate, DateOnly EndDate);

    [HttpPost("periods")]
    [Authorize(Policy = "masterdata.edit")]
    public async Task<IActionResult> AddPeriod(NewPeriod req, CancellationToken ct)
    {
        var p = await Me();
        if (!p.HasHospital(req.Hospital)) throw AppError.OutOfScope($"Hospital {req.Hospital}");
        var hospital = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(h => h.Code == req.Hospital, ct) ?? throw AppError.NotFound($"Hospital {req.Hospital}");
        var code = await runs.AddPeriodAsync(hospital.Id, req.Code, req.StartDate, req.EndDate, p, ct);
        return Created($"/api/v1/periods/{code}", new { code });
    }

    [HttpDelete("periods/{id:guid}")]
    [Authorize(Policy = "masterdata.edit")]
    public async Task<IActionResult> DeletePeriod(Guid id, CancellationToken ct)
    {
        await runs.DeletePeriodAsync(id, await Me(), ct);
        return NoContent();
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
public sealed class GovernanceController(GovernanceService governance, VeritaDbContext db) : VeritaController
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
        if (body.EntityType == "COSTING_VERSION")
        {
            var owner = await (from v in db.CostingVersions.AsNoTracking()
                               join h in db.Hospitals.AsNoTracking() on v.HospitalId equals h.Id
                               where v.Id == body.EntityId
                               select h.Code).FirstOrDefaultAsync(ct) ?? throw AppError.NotFound("Costing version");
            if (!p.HasHospital(owner)) throw AppError.OutOfScope(owner);
        }
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
