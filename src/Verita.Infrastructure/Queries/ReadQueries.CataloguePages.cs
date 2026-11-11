using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

/// <summary>
/// The Procedure/Activity and Packages lists, one page at a time. The figures on a row are worked out in memory, so
/// the filter, sort and page happen here and only the visible page (plus the type-tile counts) goes to the browser.
/// </summary>
public sealed partial class ReadQueries
{
    public sealed record CatalogueListQuery
    {
        public string? Search { get; init; }
        /// <summary>A procType, <c>CAT:{category}</c> for a category tile, or <c>ZERO_PRICE</c>.</summary>
        public string? Type { get; init; }
        public string? Sort { get; init; }
        public bool Desc { get; init; } = true;
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 20;
    }

    /// <summary>A type tile: <c>Type</c> is what the list filters on, as <see cref="CatalogueListQuery.Type"/> takes it.</summary>
    public sealed record TypeCount(string Type, int Count);
    /// <summary>
    /// <c>All</c> is the All tile (priced rows only when the list has a zero-price tile); <c>ZeroPrice</c> is null when it has none.
    /// </summary>
    public sealed record TypeCounts(int All, int? ZeroPrice, TypeCount[] Types);
    public sealed record CataloguePage<T>(T[] Rows, int Total, int Page, int PageSize, TypeCounts Counts);

    public const string ZeroPriceType = "ZERO_PRICE";
    private const string CategoryTypePrefix = "CAT:";

    public async Task<CataloguePage<ProcedureRow>> ProcedurePageAsync(ScopedRun s, Principal p, CatalogueListQuery q, CancellationToken ct)
    {
        var all = await ProcedureListAsync(s, p, ct);
        static bool Medicine(ProcedureRow x) => string.Equals(x.Category?.Trim(), ProcedureTypeMedicine, StringComparison.OrdinalIgnoreCase);

        // Tiles count every row: a zero price row only on its own tile, a medicine only on the Medicine tile.
        var priced = all.Where(x => x.ListPrice != 0).ToArray();
        var medicines = priced.Count(Medicine);
        var types = TypeTiles(priced.Where(x => !Medicine(x)).Select(x => x.ProcType));
        if (medicines > 0) types = [.. types, new TypeCount(CategoryTypePrefix + ProcedureTypeMedicine, medicines)];
        var counts = new TypeCounts(priced.Length, all.Length - priced.Length, types);

        IEnumerable<ProcedureRow> rows;
        // A search looks through everything, whichever tile is selected.
        if (!string.IsNullOrWhiteSpace(q.Search)) rows = all.Where(x => Matches(q.Search, x.Name, x.Code));
        else if (q.Type == ZeroPriceType) rows = all.Where(x => x.ListPrice == 0);
        else if (string.IsNullOrWhiteSpace(q.Type)) rows = priced;
        else if (q.Type.StartsWith(CategoryTypePrefix, StringComparison.Ordinal))
        {
            var cat = q.Type[CategoryTypePrefix.Length..];
            rows = priced.Where(x => string.Equals(x.Category?.Trim(), cat, StringComparison.OrdinalIgnoreCase));
        }
        else rows = priced.Where(x => x.ProcType == q.Type && !Medicine(x));

        var sorted = SortBy(rows, q, x => q.Sort switch
        {
            "name" => x.Name,
            "category" => x.Category,
            "procType" => x.ProcType,
            "listPrice" => x.ListPrice,
            "billedRevenue" => x.ListPrice * x.Volume,
            "revenue" => x.Revenue,
            "unitCost" => x.UnitCost,
            "totalCost" => x.TotalCost,
            "code" => ListedId(x.Code),
            _ => x.Volume,
        }, x => x.Code);
        return Paged(sorted, q, counts);
    }

    public async Task<CataloguePage<PackageRow>> PackagePageAsync(ScopedRun s, Principal p, CatalogueListQuery q, CancellationToken ct)
    {
        var all = await PackagesAsync(s, p, ct);
        var counts = new TypeCounts(all.Length, null, TypeTiles(all.Select(x => x.ProcType)));
        IEnumerable<PackageRow> rows = !string.IsNullOrWhiteSpace(q.Search) ? all.Where(x => Matches(q.Search, x.Name, x.Code))
            : string.IsNullOrWhiteSpace(q.Type) ? all
            : all.Where(x => x.ProcType == q.Type);
        var sorted = SortBy(rows, q, x => q.Sort switch
        {
            "name" => x.Name,
            "procType" => x.ProcType,
            "compositionPrice" => x.CompositionPrice,
            "price" => x.Price,
            "billedRevenue" => x.Price * x.Cases,
            "revenue" => x.Revenue,
            "unitCost" => x.UnitCost,
            "totalCost" => x.TotalCost,
            "code" => ListedId(x.Code),
            _ => x.Cases,
        }, x => x.Code);
        return Paged(sorted, q, counts);
    }

    /// <summary>Most rows first, then A to Z, as the tiles have always been ordered.</summary>
    private static TypeCount[] TypeTiles(IEnumerable<string?> procTypes) =>
        procTypes.Select(t => t?.Trim() ?? "").Where(t => t.Length > 0)
            .GroupBy(t => t).Select(g => new TypeCount(g.Key, g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Type, StringComparer.Ordinal).ToArray();

    /// <summary>Case-insensitive substring of name or code, including a hospital-prefixed code.</summary>
    private static bool Matches(string search, string? name, string? code)
    {
        var term = search.Trim();
        return (name ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) || (code ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The HIS id the list shows: the code without the hospital prefix.</summary>
    private static string ListedId(string code) => code.IndexOf('.') is var i and >= 0 ? code[(i + 1)..] : code;

    /// <summary>
    /// The first click on a number sorts largest first, on text A to Z; the caller says which. Ties keep code order so a
    /// page boundary never shuffles between requests.
    /// </summary>
    private static IOrderedEnumerable<T> SortBy<T>(IEnumerable<T> rows, CatalogueListQuery q, Func<T, object?> key, Func<T, string> tieBreak)
    {
        var comparer = Comparer<object?>.Create(CompareValues);
        var ordered = q.Desc ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        return ordered.ThenBy(tieBreak, StringComparer.Ordinal);
    }

    private static int CompareValues(object? a, object? b) => (a, b) switch
    {
        (decimal x, decimal y) => x.CompareTo(y),
        (int x, int y) => x.CompareTo(y),
        (string x, string y) => string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase),
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase),
    };

    private static CataloguePage<T> Paged<T>(IEnumerable<T> sorted, CatalogueListQuery q, TypeCounts counts)
    {
        var list = sorted as IList<T> ?? sorted.ToList();
        var size = Math.Clamp(q.PageSize, 1, 500);
        var pages = Math.Max(1, (list.Count + size - 1) / size);
        var page = Math.Clamp(q.Page, 1, pages);
        return new CataloguePage<T>(list.Skip((page - 1) * size).Take(size).ToArray(), list.Count, page, size, counts);
    }
}
