using System.Globalization;

namespace HisExtract;

/// <summary>
/// Invariant yyyy-MM-dd (and yyyy-MM) so from/to work on ar-SA HIS PCs.
/// --to is inclusive; SQL still uses an exclusive end.
/// </summary>
public static class Dates
{
    private static readonly string[] DayFormats = ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy"];
    private static readonly string[] MonthFormats = ["yyyy-MM", "yyyy-M"];

    public static DateTime? TryDay(string? v, bool endOfMonthIfYearMonth = false)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (DateTime.TryParseExact(v, DayFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.Date;
        if (DateTime.TryParseExact(v, MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            month = new DateTime(month.Year, month.Month, 1);
            return endOfMonthIfYearMonth ? month.AddMonths(1).AddDays(-1) : month;
        }
        return null;
    }

    public static bool TryMonth(string? v, out DateTime monthStart)
    {
        monthStart = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        if (v.Length == 7 && v[4] == '-' && DateTime.TryParseExact(v + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            monthStart = d.Date;
            return true;
        }
        if (DateTime.TryParseExact(v, MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        {
            monthStart = new DateTime(m.Year, m.Month, 1);
            return true;
        }
        return false;
    }

    public static DateTime LastDayOfMonth(DateTime d) =>
        new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    public static DateTime ExclusiveEnd(DateTime inclusiveTo) => inclusiveTo.Date.AddDays(1);
}
