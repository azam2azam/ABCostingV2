namespace HisExtract;

/// <summary>
/// Splits an extract window into calendar months. SQL still uses [from, toExclusive).
/// Each month becomes its own facts zip even when the user passed a multi-month range.
/// </summary>
public static class MonthSlices
{
    public readonly record struct Slice(DateTime From, DateTime ToExclusive, string YearMonth);

    public static IReadOnlyList<Slice> Of(DateTime from, DateTime toExclusive)
    {
        if (toExclusive <= from)
            throw new ArgumentException($"Period is empty: {from:yyyy-MM-dd} .. {toExclusive:yyyy-MM-dd} (end exclusive).");

        var lastInclusive = toExclusive.AddDays(-1);
        var month = new DateTime(from.Year, from.Month, 1);
        var lastMonth = new DateTime(lastInclusive.Year, lastInclusive.Month, 1);
        var list = new List<Slice>();
        while (month <= lastMonth)
        {
            var monthEnd = month.AddMonths(1);
            var sliceFrom = from > month ? from : month;
            var sliceTo = toExclusive < monthEnd ? toExclusive : monthEnd;
            if (sliceFrom < sliceTo)
                list.Add(new Slice(sliceFrom, sliceTo, month.ToString("yyyy-MM")));
            month = monthEnd;
        }
        return list;
    }
}
