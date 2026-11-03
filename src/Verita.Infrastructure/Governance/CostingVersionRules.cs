using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Governance;

/// <summary>
/// Drafts may overlap while alternatives are worked on, but a hospital's published costing models
/// never do: the model that applies to a date must be unique.
/// </summary>
public static class CostingVersionRules
{
    public static async Task<CostingVersion?> PublishedOverlapAsync(VeritaDbContext db, CostingVersion version, CancellationToken ct)
    {
        var to = version.EffectiveTo ?? DateOnly.MaxValue;
        return (await db.CostingVersions.AsNoTracking()
                .Where(v => v.HospitalId == version.HospitalId && v.Id != version.Id && v.Status == "PUBLISHED").ToListAsync(ct))
            .Where(v => v.EffectiveFrom <= to && (v.EffectiveTo ?? DateOnly.MaxValue) >= version.EffectiveFrom)
            .OrderBy(v => v.EffectiveFrom).FirstOrDefault();
    }

    public static async Task EnsureCanPublishAsync(VeritaDbContext db, CostingVersion version, CancellationToken ct)
    {
        if (await PublishedOverlapAsync(db, version, ct) is { } other)
            throw AppError.Conflict("VERSION_OVERLAP",
                $"{version.Code} ({Dates(version)}) overlaps published {other.Code} ({Dates(other)}). Change the dates so they don't overlap before publishing.");
    }

    private static string Dates(CostingVersion v) => $"{v.EffectiveFrom:yyyy-MM-dd} to {(v.EffectiveTo is { } to ? to.ToString("yyyy-MM-dd") : "open")}";
}
