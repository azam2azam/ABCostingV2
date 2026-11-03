using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ Costing models: Direct/Indirect compared between two versions (FR-10.6) ═════════
    // Laid out like the Direct and Indirect costing screens, with a From and a To value on
    // every line. A procedure's value is what costing would use: its own %, else its type's,
    // else All's.

    public sealed record CompareTypeRow(string Code, string Name, int Procedures, decimal? FromPct, decimal? ToPct, int Changed);
    public sealed record CompareItem(Guid Id, string Code, string Name, decimal ListPrice, decimal? FromPct, decimal? ToPct);
    public sealed record CompareItemsPage(CompareItem[] Items, int Total);
    public sealed record CompareOverheadRow(string CostCenterCode, string CostCenterName, string Description,
        decimal? FromPct, decimal? ToPct, decimal? FromAmount, decimal? ToAmount);

    private sealed record CompareSide(Dictionary<string, decimal> Types, Dictionary<Guid, decimal> Items)
    {
        public decimal? Type(string code) => Types.TryGetValue(code, out var v) ? v : null;

        public decimal? Effective(CostingProc x)
        {
            if (Items.TryGetValue(x.Id, out var item)) return Nz(item);
            return Nz(Type(TypeKey(x)) ?? Type(ProcedureTypeAll));
        }
    }

    private static decimal? Nz(decimal? v) => v is 0 ? null : v;

    private static string TypeKey(CostingProc x) =>
        x.Medicine ? ProcedureTypeMedicine : string.IsNullOrWhiteSpace(x.ProcType) ? "UNSPECIFIED" : x.ProcType;

    private async Task<(CostingVersion From, CostingVersion To)> ComparePairAsync(Guid hospitalId, string? from, string? to, CancellationToken ct)
    {
        var f = (from ?? "").Trim();
        var t = (to ?? "").Trim();
        if (f.Length == 0 || t.Length == 0) throw AppError.Invalid("Choose a From and a To costing version.");
        var versions = await db.CostingVersions.AsNoTracking().Where(v => v.HospitalId == hospitalId && (v.Code == f || v.Code == t)).ToListAsync(ct);
        var a = versions.FirstOrDefault(v => v.Code == f) ?? throw AppError.NotFound($"Costing version {f}");
        var b = versions.FirstOrDefault(v => v.Code == t) ?? throw AppError.NotFound($"Costing version {t}");
        return (a, b);
    }

    private async Task<(CompareSide From, CompareSide To)> CompareSidesAsync(ScopedRun s, string? from, string? to, string stream,
        CancellationToken ct)
    {
        var (a, b) = await ComparePairAsync(s.Hospital.Id, from, to, ct);
        return (new CompareSide(await TypeMarginMapAsync(s.Hospital.Id, a.Id, stream, ct), await ItemMarginMapAsync(s.Hospital.Id, a.Id, stream, ct)),
            new CompareSide(await TypeMarginMapAsync(s.Hospital.Id, b.Id, stream, ct), await ItemMarginMapAsync(s.Hospital.Id, b.Id, stream, ct)));
    }

    public async Task<CompareTypeRow[]> CompareTypesAsync(ScopedRun s, Principal p, string? from, string? to, string stream,
        string? search, CancellationToken ct)
    {
        var (a, b) = await CompareSidesAsync(s, from, to, stream, ct);
        var q = (search ?? "").Trim();
        var procs = (await CostingProceduresAsync(s, p, ct)).Where(x => MatchesSearch(x, q)).ToList();
        CompareTypeRow Row(string code, string name, IEnumerable<CostingProc> group)
        {
            var list = group.ToList();
            return new CompareTypeRow(code, name, list.Count, Nz(a.Type(code)), Nz(b.Type(code)), list.Count(x => a.Effective(x) != b.Effective(x)));
        }
        var types = procs.Where(x => !x.Medicine).GroupBy(TypeKey)
            .Select(g => Row(g.Key, TypeDisplayName(g.Key), g))
            .OrderBy(x => x.Name)
            .ToList();
        return
        [
            Row(ProcedureTypeAll, "All", procs),
            .. types,
            Row(ProcedureTypeMedicine, "Medicine", procs.Where(x => x.Medicine)),
        ];
    }

    public async Task<CompareItemsPage> CompareItemsAsync(ScopedRun s, Principal p, string? from, string? to, string stream,
        string typeCode, int skip, int take, string? search, bool changedOnly, CancellationToken ct)
    {
        typeCode = (typeCode ?? "").Trim();
        if (typeCode.Length == 0 || typeCode.Equals(ProcedureTypeAll, StringComparison.OrdinalIgnoreCase))
            throw AppError.Invalid("Expand a procedure type, not All.");
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 200);
        skip = Math.Max(0, skip);
        var (a, b) = await CompareSidesAsync(s, from, to, stream, ct);
        var q = (search ?? "").Trim();
        var procs = (await CostingProceduresAsync(s, p, ct))
            .Where(x => MatchesType(x, typeCode) && MatchesSearch(x, q) && (!changedOnly || a.Effective(x) != b.Effective(x)))
            .OrderBy(x => x.Name).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = procs.Skip(skip).Take(take).ToList();
        var ids = page.Select(x => x.Id).ToHashSet();
        var asOf = s.Period.EndDate;
        var prices = (await db.ProcedureRates.AsNoTracking()
                .Where(r => r.HospitalId == s.Hospital.Id && ids.Contains(r.ProcedureId))
                .ToListAsync(ct))
            .Where(r => r.IsEffective(asOf))
            .GroupBy(r => r.ProcedureId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        var items = page.Select(x => new CompareItem(x.Id, x.Code, x.Name, prices.GetValueOrDefault(x.Id), a.Effective(x), b.Effective(x))).ToArray();
        return new CompareItemsPage(items, procs.Count);
    }

    public async Task<CompareOverheadRow[]> CompareEncounterOverheadsAsync(ScopedRun s, string? from, string? to, CancellationToken ct)
    {
        var (a, b) = await ComparePairAsync(s.Hospital.Id, from, to, ct);
        var rows = await db.EncounterOverheads.AsNoTracking()
            .Where(x => x.HospitalId == s.Hospital.Id && (x.CostingVersionId == a.Id || x.CostingVersionId == b.Id))
            .ToListAsync(ct);
        var ccIds = rows.Select(x => x.CostCenterId).Distinct().ToList();
        var centres = await db.CostCenters.AsNoTracking().Where(c => ccIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        return rows
            .GroupBy(x => (x.CostCenterId, Description: (x.Description ?? "").Trim()))
            .Select(g =>
            {
                var fa = g.FirstOrDefault(x => x.CostingVersionId == a.Id);
                var tb = g.FirstOrDefault(x => x.CostingVersionId == b.Id);
                var cc = centres.GetValueOrDefault(g.Key.CostCenterId);
                return new CompareOverheadRow(cc?.Code ?? "", cc?.Name ?? "", g.Key.Description,
                    Nz(fa?.OverheadPct), Nz(tb?.OverheadPct), Nz(fa?.OverheadAmount), Nz(tb?.OverheadAmount));
            })
            .OrderBy(x => x.CostCenterCode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Description)
            .ToArray();
    }
}
