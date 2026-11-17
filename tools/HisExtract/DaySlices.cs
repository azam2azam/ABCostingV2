namespace HisExtract;

/// <summary>
/// Splits an extract window into calendar days. Each day becomes its own facts zip, so a pack stays
/// small enough to upload and import in one go, and a failed day can be re-run on its own.
/// SQL still uses [from, toExclusive).
/// </summary>
public static class DaySlices
{
    public readonly record struct Slice(DateTime From, DateTime ToExclusive, string Day);

    public static IReadOnlyList<Slice> Of(DateTime from, DateTime toExclusive)
    {
        if (toExclusive <= from)
            throw new ArgumentException($"Period is empty: {from:yyyy-MM-dd} .. {toExclusive:yyyy-MM-dd} (end exclusive).");
        var list = new List<Slice>();
        for (var day = from.Date; day < toExclusive; day = day.AddDays(1))
            list.Add(new Slice(day, day.AddDays(1), day.ToString("yyyy-MM-dd")));
        return list;
    }
}
