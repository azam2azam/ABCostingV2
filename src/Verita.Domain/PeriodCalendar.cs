using Verita.Domain.Entities;

namespace Verita.Domain;

/// <summary>
/// A hospital's periods may overlap: next to its calendar months it can hold custom periods for any dates
/// (Ramadan, a quarter, a fortnight). A custom period is one that is not a calendar month and overlaps another.
/// </summary>
public static class PeriodCalendar
{
    public static bool IsMonth(DateOnly start, DateOnly end) => start.Day == 1 && end == start.AddMonths(1).AddDays(-1);

    public static bool IsMonth(Period p) => IsMonth(p.StartDate, p.EndDate);

    public static bool Overlaps(Period a, Period b) => a.Id != b.Id && a.StartDate <= b.EndDate && a.EndDate >= b.StartDate;

    /// <summary>Not a calendar month, and overlapping another of the hospital's periods.</summary>
    public static bool IsCustom(Period p, IEnumerable<Period> hospitalPeriods) =>
        !IsMonth(p) && hospitalPeriods.Any(o => Overlaps(p, o));
}
