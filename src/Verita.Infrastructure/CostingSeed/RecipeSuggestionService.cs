using Microsoft.EntityFrameworkCore;
using Verita.Domain.Entities;
using Verita.Application;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.CostingSeed;

/// <summary>
/// Computes recipe suggestions from imported HIS facts and the seed templates. Does not write rows.
/// </summary>
public sealed class RecipeSuggestionService(IDbContextFactory<VeritaDbContext> factory)
{
    /// <summary>Rates and capacity are dated at the costing-year start, not the facts month (HIS-RESOURCE-RATE-SEED).</summary>
    public static DateOnly TemplateFrom(DateOnly from) => new(from.Year, 1, 1);

    /// <summary>
    /// The published versions that cover the imported facts. Same IsEffective rule a run uses.
    /// </summary>
    public static CostingVersion[] VersionsCoveringFacts(IEnumerable<CostingVersion> published, IEnumerable<DateOnly> monthStarts)
    {
        var asOfs = monthStarts.Select(m => new DateOnly(m.Year, m.Month, DateTime.DaysInMonth(m.Year, m.Month))).ToArray();
        if (asOfs.Length == 0) return [];
        return published.Where(v => asOfs.Any(d => v.IsEffective(d))).OrderBy(v => v.EffectiveFrom).ThenBy(v => v.Code).ToArray();
    }

    public async Task<RecipeSuggestionList> ListAsync(Principal p, string hospital, string? entity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hospital)) throw AppError.Invalid("Pick a hospital.");
        if (!p.HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");

        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct)
            ?? throw AppError.NotFound($"Hospital {hospital}");

        var ccDept = await (
            from c in db.CostCenters.AsNoTracking()
            join d in db.Departments.AsNoTracking() on c.DepartmentId equals d.Id
            where c.HospitalId == h.Id
            select new { c.Id, c.Code, c.Name, Dept = d.Code, c.IsActive }).ToListAsync(ct);

        var centres = ccDept.Select(c => new HubMatcher.Centre(c.Id, c.Code, c.Name, c.IsActive)).ToList();
        var op = await (
            from e in db.Encounters.AsNoTracking()
            join cc in db.CostCenters.AsNoTracking() on e.DepartmentId equals cc.DepartmentId
            where e.HospitalId == h.Id && (e.EncType == "OP" || e.EncType == "OUTPATIENT")
            group e by cc.Id into g
            select new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var hits = HubMatcher.Match(centres, null, op);
        var byId = ccDept.ToDictionary(x => x.Id);
        var hubs = hits.Select(x =>
        {
            if (x.CostCenterId is not { } id || !byId.TryGetValue(id, out var cc))
                return new RecipeSuggestions.HubCodes(x.Token, null, null);
            return new RecipeSuggestions.HubCodes(x.Token, cc.Code, cc.Dept);
        }).ToList();

        var dentalRow = ccDept
            .Where(x => x.IsActive && x.Name.Contains("DENTAL", StringComparison.OrdinalIgnoreCase) && !HubMatcher.IsJunk(x.Name, x.Code))
            .OrderBy(x => x.Name.Length).ThenBy(x => x.Code)
            .FirstOrDefault();
        RecipeSuggestions.HubCodes? dental = dentalRow is null ? null : new("DENTAL", dentalRow.Code, dentalRow.Dept);

        var activityCodes = (await db.Activities.AsNoTracking().Where(a => a.HospitalId == h.Id).Select(a => a.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceCodes = (await db.Resources.AsNoTracking().Where(r => r.HospitalId == h.Id).Select(r => r.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recipeKeys = (await (
            from ar in db.ActivityResources.AsNoTracking()
            join a in db.Activities.AsNoTracking() on ar.ActivityId equals a.Id
            join r in db.Resources.AsNoTracking() on ar.ResourceId equals r.Id
            where a.HospitalId == h.Id
            select a.Code + "|" + r.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var versions = await db.CostingVersions.AsNoTracking().Where(v => v.HospitalId == h.Id)
            .OrderByDescending(v => v.EffectiveFrom).ThenBy(v => v.Code).ToListAsync(ct);
        var monthDates = await db.Encounters.AsNoTracking()
            .Where(e => e.HospitalId == h.Id)
            .Select(e => e.AdmitAt)
            .ToListAsync(ct);
        var months = monthDates.Where(d => d != default).Select(d => new DateOnly(d.Year, d.Month, 1)).Distinct().OrderBy(d => d).ToList();
        var latestMonth = months.Count == 0 ? (DateOnly?)null : months[^1];
        var from = TemplateFrom(latestMonth ?? new DateOnly(2026, 1, 1));
        var covering = latestMonth is { } m
            ? VersionsCoveringFacts(versions.Where(v => v.Status == "PUBLISHED"), [m])
            : [];
        var cv = versions.FirstOrDefault(v => v.Status == "DRAFT")
            ?? covering.LastOrDefault()
            ?? versions.FirstOrDefault(v => v.Status == "PUBLISHED")
            ?? versions.FirstOrDefault();

        var boundQuery =
            from d in db.CostingDefinitions.AsNoTracking()
            join l in db.ProcedureActivityLines.AsNoTracking() on d.Id equals l.DefinitionId
            join a in db.Activities.AsNoTracking() on l.ActivityId equals a.Id
            join proc in db.Procedures.AsNoTracking() on d.ProcedureId equals proc.Id
            where proc.HospitalId == h.Id
            select new { d.CostingVersionId, Proc = proc.Code, Act = a.Code };
        if (cv is not null)
            boundQuery = boundQuery.Where(x => x.CostingVersionId == cv.Id);
        var boundKeys = (await boundQuery.Select(x => x.Proc + "|" + x.Act).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var billedRows = await (
            from s in db.EncounterServices.AsNoTracking()
            join e in db.Encounters.AsNoTracking() on s.EncounterId equals e.Id
            join proc in db.Procedures.AsNoTracking() on s.ProcedureId equals proc.Id
            where e.HospitalId == h.Id && proc.IsActive
            group s by new { proc.Code, proc.Name, proc.Category, proc.ProcType } into g
            select new { g.Key.Code, g.Key.Name, g.Key.Category, g.Key.ProcType, N = g.Count() }
        ).ToListAsync(ct);
        var billed = billedRows
            .Select(x => new RecipeSuggestions.BilledProc(x.Code, x.Name, x.Category, x.ProcType, x.N))
            .ToList();

        var roleRows = await (
            from s in db.EncounterServices.AsNoTracking()
            join e in db.Encounters.AsNoTracking() on s.EncounterId equals e.Id
            join proc in db.Procedures.AsNoTracking() on s.ProcedureId equals proc.Id
            join prv in db.Providers.AsNoTracking() on s.ProviderId equals prv.Id
            join r in db.Resources.AsNoTracking() on prv.ResourceId equals r.Id
            where e.HospitalId == h.Id && s.ProviderId != null && proc.IsActive
            group s by new { ProcName = proc.Name, proc.Category, proc.ProcType, ResCode = r.Code, ResName = r.Name } into g
            select new { g.Key.ProcName, g.Key.Category, g.Key.ProcType, g.Key.ResCode, g.Key.ResName, N = g.Count() }
        ).ToListAsync(ct);

        var invoiceRoles = roleRows
            .Select(x => new { Family = ProcedureFamily.Of(x.ProcName, x.Category, x.ProcType), x.ResCode, x.ResName, x.N })
            .Where(x => x.Family != ProcedureFamily.Skip)
            .GroupBy(x => x.Family)
            .Select(g =>
            {
                var top = g.GroupBy(x => new { x.ResCode, x.ResName })
                    .Select(x => new { x.Key.ResCode, x.Key.ResName, N = x.Sum(i => i.N) })
                    .OrderByDescending(x => x.N)
                    .First();
                return new RecipeSuggestions.InvoiceRole(g.Key, top.ResCode, top.ResName, top.N);
            })
            .ToList();

        var input = new RecipeSuggestions.Input(
            h.Code, from, cv?.Code, hubs, dental,
            activityCodes, resourceCodes, recipeKeys, boundKeys, billed, invoiceRoles);
        return RecipeSuggestions.Build(input, entity);
    }
}
