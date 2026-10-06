using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Governance;

/// <summary>
/// The audit trail (FR-10.9, FR-10.10). A failed write is logged and swallowed (FR-10.12): trading
/// a complete trail for an outage is the wrong way round, and the trail is reconstructible from
/// run results.
/// </summary>
public sealed class AuditWriter(IDbContextFactory<VeritaDbContext> factory, IRequestInfo request, ILogger<AuditWriter> log) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task WriteAsync(Principal? actor, string action, string entityType, string? entityId, string? entityLabel,
        object? before = null, object? after = null, object? metadata = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.AuditLogs.Add(new AuditLog
            {
                Id = IdGen.Sequential(), UserId = actor?.Id, ActorEmail = actor?.Email ?? "system", Action = action,
                EntityType = entityType, EntityId = entityId, EntityLabel = entityLabel,
                BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
                AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
                Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata, Json),
                IpAddress = request.IpAddress, At = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Audit write failed for {Action} {EntityType} {EntityId}", action, entityType, entityId);
        }
    }
}

public sealed class NoRequestInfo : IRequestInfo
{
    public string? IpAddress => null;
}
