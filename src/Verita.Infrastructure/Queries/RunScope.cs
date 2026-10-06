using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Queries;

/// <summary>ARCH-12: every response names the run, its status, version, period and hospital.</summary>
public sealed record Meta(
    Guid? RunId, string? Run, string? RunStatus, string? CostingVersion, string? Period, string? Hospital, string? HospitalName,
    string? Currency, DateTime GeneratedAt);

public sealed record Envelope<T>(Meta Meta, T Data);

public sealed record ScopedRun(CalculationRun Run, Hospital Hospital, Period Period, CostingVersion Version)
{
    public Meta Meta() => new(Run.Id, Run.Code, Run.Status, Version.Code, Period.Code, Hospital.Code, Hospital.Name, Hospital.Currency, DateTime.UtcNow);
}

/// <summary>
/// Run selection (ARCH-14): ?run=&lt;id|code&gt;, or ?hospital=&amp;period=. With neither, the most
/// recent published run in the caller's scope — never an unpublished one, which would hand
/// unapproved numbers to an integration nobody is watching. Out-of-scope runs answer 403, not
/// an empty result (FR-0.6).
/// </summary>
public static class RunScope
{
    public static async Task<ScopedRun> ResolveAsync(VeritaDbContext db, Principal p, string? run, string? hospital, string? period, CancellationToken ct)
    {
        CalculationRun? r;
        if (!string.IsNullOrWhiteSpace(run))
        {
            r = Guid.TryParse(run, out var id)
                ? await db.CalculationRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
                : await db.CalculationRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Code == run, ct);
            if (r is null) throw AppError.RunNotFound(run);
        }
        else
        {
            var q = from x in db.CalculationRuns.AsNoTracking()
                    join h in db.Hospitals on x.HospitalId equals h.Id
                    join pe in db.Periods on x.PeriodId equals pe.Id
                    where x.RunType != "SIMULATION"
                    select new { x, h.Code, Period = pe.Code, pe.EndDate };
            if (!string.IsNullOrWhiteSpace(hospital)) q = q.Where(z => z.Code == hospital);
            if (!string.IsNullOrWhiteSpace(period)) q = q.Where(z => z.Period == period);
            var candidates = await q.ToListAsync(ct);
            candidates = candidates.Where(z => p.HasHospital(z.Code)).ToList();
            var explicitPair = !string.IsNullOrWhiteSpace(hospital) && !string.IsNullOrWhiteSpace(period);
            var pick = explicitPair
                ? candidates.OrderByDescending(z => z.x.Status == "PUBLISHED").ThenByDescending(z => z.x.CreatedAt).FirstOrDefault()
                : candidates.Where(z => z.x.Status == "PUBLISHED").OrderByDescending(z => z.EndDate).ThenBy(z => z.Code).FirstOrDefault();
            if (pick is null)
                throw AppError.NotCosted(explicitPair
                    ? $"No run exists for {hospital} {period}."
                    : "No published run is in your scope. Name a run explicitly with ?run= to read an unpublished one.");
            r = pick.x;
        }

        var hosp = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == r.HospitalId, ct);
        if (!p.HasHospital(hosp.Code)) throw AppError.OutOfScope($"Run {r.Code}");
        var per = await db.Periods.AsNoTracking().FirstAsync(x => x.Id == r.PeriodId, ct);
        var ver = await db.CostingVersions.AsNoTracking().FirstAsync(x => x.Id == r.CostingVersionId, ct);
        return new ScopedRun(r, hosp, per, ver);
    }

    public static async Task<string[]> HospitalCodesInScope(VeritaDbContext db, Principal p, CancellationToken ct) =>
        (await db.Hospitals.AsNoTracking().Select(h => h.Code).ToListAsync(ct)).Where(p.HasHospital).ToArray();
}
