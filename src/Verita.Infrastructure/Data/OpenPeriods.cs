using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Data;

/// <summary>Accounting months (yyyy-MM). Shared by HIS facts import and costing seed.</summary>
public static class OpenPeriods
{
    public static IEnumerable<DateOnly> Months(DateOnly from, DateOnly toInclusive)
    {
        if (toInclusive < from) (from, toInclusive) = (toInclusive, from);
        for (var d = new DateOnly(from.Year, from.Month, 1);
             d <= new DateOnly(toInclusive.Year, toInclusive.Month, 1);
             d = d.AddMonths(1))
            yield return d;
    }

    public static async Task<string[]> EnsureAsync(VeritaDbContext db, IEnumerable<DateOnly> months, CancellationToken ct)
    {
        var created = new List<string>();
        var existing = (await db.Periods.Select(p => p.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var month in months.Select(m => new DateOnly(m.Year, m.Month, 1)).Distinct())
        {
            var code = month.ToString("yyyy-MM");
            if (!existing.Add(code)) continue;
            var end = month.AddMonths(1).AddDays(-1);
            db.Periods.Add(new Period
            {
                Id = IdGen.Sequential(), Code = code, StartDate = month, EndDate = end,
                Days = end.DayNumber - month.DayNumber + 1, Status = "OPEN",
            });
            created.Add(code);
        }
        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created.ToArray();
    }

    /// <summary>
    /// A hospital with its own calendar has one period per calendar month, named like "2026 Sep".
    /// Facts for a month it does not have yet add that month; existing periods are never stretched.
    /// </summary>
    public static async Task<string[]> EnsureHospitalMonthsAsync(VeritaDbContext db, Guid hospitalId, DateOnly from, DateOnly toInclusive, CancellationToken ct)
    {
        var owned = await db.Periods.Where(p => p.HospitalId == hospitalId).ToListAsync(ct);
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == hospitalId, ct);
        var codes = (await db.Periods.Select(p => p.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        foreach (var month in Months(from, toInclusive))
        {
            var end = month.AddMonths(1).AddDays(-1);
            if (owned.Any(p => p.StartDate <= end && p.EndDate >= month)) continue;
            var code = month.ToString("yyyy MMM", CultureInfo.InvariantCulture);
            if (codes.Contains(code)) code = $"{hospital.Code} {code}";
            db.Periods.Add(new Period
            {
                Id = IdGen.Sequential(), Code = code, StartDate = month, EndDate = end,
                Days = end.DayNumber - month.DayNumber + 1, Status = "OPEN", HospitalId = hospitalId,
            });
            codes.Add(code);
            created.Add(code);
        }
        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created.ToArray();
    }
}
