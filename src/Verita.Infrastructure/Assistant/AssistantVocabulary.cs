using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Application.Assistant;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Assistant;

/// <summary>
/// The names one user's questions are matched against. Hospitals and periods come from the
/// database on each request (they are few and scope-dependent). A hospital's procedures and
/// packages (tens of thousands) are indexed once and cached for ten minutes.
/// </summary>
public sealed class AssistantVocabulary : IAssistantVocabulary
{
    private static readonly Lexicon StaticLexicon = new(Knowledge.StaticEntries());
    private static readonly ConcurrentDictionary<Guid, (DateTime At, Lexicon Lexicon)> HospitalCache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<VeritaDbContext> _factory;
    private readonly Dictionary<string, Guid> _hospitalIds;
    private readonly Dictionary<string, IReadOnlyList<PeriodRef>> _periods;

    public Lexicon Static => StaticLexicon;
    public Lexicon Hospitals { get; }
    public string? DefaultHospital { get; }

    private AssistantVocabulary(IDbContextFactory<VeritaDbContext> factory, Lexicon hospitals, Dictionary<string, Guid> ids,
        Dictionary<string, IReadOnlyList<PeriodRef>> periods, string? defaultHospital)
    {
        _factory = factory;
        Hospitals = hospitals;
        _hospitalIds = ids;
        _periods = periods;
        DefaultHospital = defaultHospital;
    }

    public static async Task<AssistantVocabulary> ForAsync(IDbContextFactory<VeritaDbContext> factory, Principal p, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var hospitals = (await db.Hospitals.AsNoTracking().OrderBy(h => h.Code).ToListAsync(ct)).Where(h => p.HasHospital(h.Code)).ToList();
        var ids = hospitals.Select(h => h.Id).ToList();
        var runs = await (from r in db.CalculationRuns.AsNoTracking()
                          join pe in db.Periods on r.PeriodId equals pe.Id
                          where r.RunType != "SIMULATION" && ids.Contains(r.HospitalId)
                          select new { r.HospitalId, r.PeriodId, r.Status, pe.EndDate }).ToListAsync(ct);
        // A period belongs to one hospital, or (HospitalId null) to all of them.
        var periodRows = await db.Periods.AsNoTracking().Where(x => x.HospitalId == null || ids.Contains(x.HospitalId.Value)).ToListAsync(ct);
        var withRun = runs.Select(r => (r.HospitalId, r.PeriodId)).ToHashSet();
        var periods = hospitals.ToDictionary(h => h.Code, h => (IReadOnlyList<PeriodRef>)periodRows.Where(x => x.HospitalId == h.Id || x.HospitalId == null)
            .OrderBy(x => x.StartDate).Select(x => new PeriodRef(x.Code, x.StartDate, x.EndDate, withRun.Contains((h.Id, x.Id)))).ToList());
        // Same rule as RunScope with no parameters: the latest published run, ties broken by hospital code.
        var latest = runs.Where(r => r.Status == "PUBLISHED").OrderByDescending(r => r.EndDate)
            .ThenBy(r => hospitals.First(h => h.Id == r.HospitalId).Code).FirstOrDefault()
            ?? runs.OrderByDescending(r => r.EndDate).FirstOrDefault();
        var lexicon = new Lexicon(hospitals.Select(h => new LexEntry("hospital", h.Code, h.Name, [h.Name, h.Code])));
        return new AssistantVocabulary(factory, lexicon, hospitals.ToDictionary(h => h.Code, h => h.Id), periods,
            latest is null ? hospitals.FirstOrDefault()?.Code : hospitals.First(h => h.Id == latest.HospitalId).Code);
    }

    public IReadOnlyList<PeriodRef> Periods(string hospital) => _periods.GetValueOrDefault(hospital) ?? [];

    public Lexicon ForHospital(string hospital)
    {
        if (!_hospitalIds.TryGetValue(hospital, out var id)) return new Lexicon([]);
        if (HospitalCache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < Ttl) return hit.Lexicon;
        using var db = _factory.CreateDbContext();
        var packages = db.Packages.AsNoTracking().Where(x => x.HospitalId == id).Select(x => new { x.Id, x.Code, x.Name }).ToList();
        var packageCodes = packages.Select(x => x.Code).ToHashSet();
        // A package is also billed as a procedure of the same code; it is answered as a package.
        var procedures = db.Procedures.AsNoTracking().Where(x => x.HospitalId == id).Select(x => new { x.Id, x.Code, x.Name }).ToList()
            .Where(x => !packageCodes.Contains(x.Code));
        var lexicon = new Lexicon(
            procedures.Select(x => new LexEntry("procedure", x.Id.ToString(), x.Name, [x.Name], x.Code))
                .Concat(packages.Select(x => new LexEntry("package", x.Id.ToString(), x.Name, [x.Name], x.Code))));
        HospitalCache[id] = (DateTime.UtcNow, lexicon);
        return lexicon;
    }

    /// <summary>After an import or data entry, so new names are found at once.</summary>
    public static void Invalidate() => HospitalCache.Clear();
}
