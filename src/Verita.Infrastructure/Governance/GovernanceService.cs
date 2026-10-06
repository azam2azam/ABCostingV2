using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Runs;

namespace Verita.Infrastructure.Governance;

/// <summary>
/// Four-eye approval (FR-10). Every rule here is enforced in the service layer, not only in the
/// interface: the requester can never be the approver (FR-0.1, FR-10.3), and each approval
/// stage needs its own permission.
/// </summary>
public sealed class GovernanceService(IDbContextFactory<VeritaDbContext> factory, RunService runs, IAuditWriter audit)
{
    public static string PermissionFor(string stage) => stage switch
    {
        "FINANCE_APPROVAL" => "approval.finance",
        "CLINICAL_APPROVAL" => "approval.clinical",
        _ => "approval.request",
    };

    public async Task<ApprovalRequest> RequestAsync(string entityType, Guid entityId, string note, Principal actor, CancellationToken ct = default)
    {
        if (!actor.Can("approval.request")) throw AppError.Forbidden("approval.request");
        if (!Catalogue.ApprovalFlow.TryGetValue(entityType, out var flow)) throw AppError.Invalid($"{entityType} has no approval workflow.");
        if (string.IsNullOrWhiteSpace(note)) throw AppError.Invalid("A request note is required: say what changed and why.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ApprovalRequests.AnyAsync(a => a.EntityId == entityId && a.Stage != "PUBLISHED" && a.Stage != "REJECTED", ct))
            throw AppError.Conflict("APPROVAL_PENDING", "An approval request is already open for this item.");

        string label;
        Guid? versionId = null;
        if (entityType == "CALCULATION_RUN")
        {
            var run = await db.CalculationRuns.FirstOrDefaultAsync(r => r.Id == entityId, ct) ?? throw AppError.RunNotFound(entityId.ToString());
            if (run.Status != "RECONCILED") throw AppError.Conflict("ILLEGAL_TRANSITION", $"Only a RECONCILED run can be put up for approval; this one is {run.Status}.");
            var blockers = await runs.PublishBlockersAsync(db, run, ct);
            if (blockers.Count > 0) throw AppError.Conflict("PUBLISH_BLOCKED", "This run could never be published, so it cannot be put up for approval.", new { reasons = blockers });
            label = run.Name;
            run.Status = "REVIEWED";
        }
        else if (entityType == "COSTING_VERSION")
        {
            var v = await db.CostingVersions.FirstOrDefaultAsync(x => x.Id == entityId, ct) ?? throw AppError.NotFound("Costing version");
            if (v.Status == "PUBLISHED") throw AppError.Conflict("VERSION_IMMUTABLE", "A published costing version is never edited; create a new version (FR-10.5).");
            label = v.Name;
            versionId = v.Id;
            v.Status = "REVIEW";
        }
        else label = entityType;

        // The request lands on the first approval stage after review.
        var stage = flow[Math.Min(2, flow.Length - 1)];
        var req = new ApprovalRequest
        {
            Id = IdGen.Sequential(), EntityType = entityType, EntityId = entityId, EntityLabel = label, CostingVersionId = versionId,
            Stage = stage, RequestedById = actor.Id, RequestNote = note.Trim(), RequestedAt = DateTime.UtcNow,
        };
        db.ApprovalRequests.Add(req);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "UPDATE", "ApprovalRequest", req.Id.ToString(), label, after: new { stage, entityType }, ct: ct);
        return req;
    }

    public async Task<ApprovalRequest> DecideAsync(Guid requestId, bool approve, string? note, Principal actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var req = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == requestId, ct) ?? throw AppError.NotFound("Approval request");
        if (req.Stage is "PUBLISHED" or "REJECTED" or "RETIRED") throw AppError.Conflict("APPROVAL_CLOSED", $"This request is already {req.Stage}.");

        var permission = PermissionFor(req.Stage);
        if (!actor.Can(permission)) throw AppError.Forbidden(permission);
        // FR-0.1: enforced here, whatever the interface shows.
        if (req.RequestedById == actor.Id)
            throw new AppError("SELF_APPROVAL", "The requester of an approval can never be its approver (FR-0.1).", 403);

        var before = req.Stage;
        req.ReviewedById = actor.Id;
        req.ReviewedAt = DateTime.UtcNow;
        req.ReviewNote = note;

        if (!approve)
        {
            req.Stage = "REJECTED";
            if (req.EntityType == "CALCULATION_RUN")
                await db.CalculationRuns.Where(r => r.Id == req.EntityId && r.Status == "REVIEWED").ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "RECONCILED"), ct);
            if (req.EntityType == "COSTING_VERSION")
                await db.CostingVersions.Where(v => v.Id == req.EntityId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, "DRAFT"), ct);
        }
        else
        {
            var next = Catalogue.NextStage(req.EntityType, req.Stage) ?? "PUBLISHED";
            req.Stage = next;
            if (next == "PUBLISHED") await ApplyFinalApprovalAsync(db, req, actor, ct);
            else if (req.EntityType == "COSTING_VERSION" && before == "FINANCE_APPROVAL")
                await db.CostingVersions.Where(v => v.Id == req.EntityId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, "FINANCE_APPROVED"), ct);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, approve ? "APPROVE" : "REJECT", "ApprovalRequest", req.Id.ToString(), req.EntityLabel,
            new { stage = before }, new { stage = req.Stage, note }, ct: ct);
        return req;
    }

    private static async Task ApplyFinalApprovalAsync(VeritaDbContext db, ApprovalRequest req, Principal actor, CancellationToken ct)
    {
        switch (req.EntityType)
        {
            case "CALCULATION_RUN":
                // Approved, not yet published: publication is its own act with its own gates.
                await db.CalculationRuns.Where(r => r.Id == req.EntityId && r.Status == "REVIEWED")
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "APPROVED").SetProperty(r => r.ApprovedBy, actor.Email), ct);
                break;
            case "COSTING_VERSION":
                await db.CostingVersions.Where(v => v.Id == req.EntityId)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, "PUBLISHED").SetProperty(v => v.PublishedAt, DateTime.UtcNow).SetProperty(v => v.PublishedById, actor.Id), ct);
                break;
        }
    }
}
