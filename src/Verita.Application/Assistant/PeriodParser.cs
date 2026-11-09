namespace Verita.Application.Assistant;

public sealed record PeriodRef(string Code, DateOnly Start, DateOnly End, bool HasRun);

public sealed record PeriodMatch(string Code, double Score, int[] Positions, string? Note);

/// <summary>
/// Reads a period from the question: a month name with or without a year ("October",
/// "oct 2026", "Nov 26"), a numeric month ("2026-08", "08/2026"), a period code, or a relative
/// phrase ("latest", "last month", "previous period"). Resolved against the hospital's own
/// periods by start date, because period codes differ between hospitals ("2026-08", "2026 Nov").
/// </summary>
public static class PeriodParser
{
    private static readonly string[][] Months =
    [
        ["jan", "january"], ["feb", "february"], ["mar", "march"], ["apr", "april"], ["may"], ["jun", "june"],
        ["jul", "july"], ["aug", "august"], ["sep", "sept", "september"], ["oct", "october"], ["nov", "november"], ["dec", "december"],
    ];

    private static int Month(string t)
    {
        for (var i = 0; i < Months.Length; i++) if (Months[i].Contains(t)) return i + 1;
        return 0;
    }

    private static int Year(string t) =>
        t.Length == 4 && int.TryParse(t, out var y) && y is >= 2000 and <= 2099 ? y
        : t.Length == 2 && int.TryParse(t, out var yy) && yy is >= 15 and <= 60 ? 2000 + yy : 0;

    /// <param name="tokens">Normalised tokens (not stemmed).</param>
    /// <param name="periods">The hospital's periods.</param>
    /// <param name="anchor">The period a relative phrase is relative to: the thread's or the screen's.</param>
    public static PeriodMatch? Find(string[] tokens, IReadOnlyList<PeriodRef> periods, string? anchor)
    {
        if (periods.Count == 0) return null;
        var ordered = periods.OrderBy(p => p.Start).ToList();
        var withRun = ordered.Where(p => p.HasRun).ToList();
        var latest = (withRun.Count > 0 ? withRun : ordered)[^1];

        // A period code said outright ("2026 Nov", "2026-08").
        for (var i = 0; i < tokens.Length; i++)
            for (var n = 3; n >= 1; n--)
            {
                if (i + n > tokens.Length) continue;
                var joined = string.Concat(tokens.Skip(i).Take(n));
                var hit = ordered.FirstOrDefault(p => AssistantText.Compact(p.Code) == joined);
                if (hit is not null && (n > 1 || joined.Any(char.IsDigit) && joined.Length >= 6))
                    return new(hit.Code, 1.0, Enumerable.Range(i, n).ToArray(), null);
            }

        // Month name, optionally with a year either side.
        for (var i = 0; i < tokens.Length; i++)
        {
            var m = Month(tokens[i]);
            if (m == 0) continue;
            // "may" is also a verb; take it only beside a year or after "in"/"for".
            if (tokens[i] == "may" && !(i + 1 < tokens.Length && Year(tokens[i + 1]) > 0) && !(i > 0 && tokens[i - 1] is "in" or "for" or "of")) continue;
            int year = 0; var pos = new List<int> { i };
            if (i + 1 < tokens.Length && Year(tokens[i + 1]) is var y1 and > 0) { year = y1; pos.Add(i + 1); }
            else if (i > 0 && Year(tokens[i - 1]) is var y0 and > 0) { year = y0; pos.Add(i - 1); }
            var hit = ordered.Where(p => p.Start.Month == m && (year == 0 || p.Start.Year == year))
                .OrderByDescending(p => p.HasRun).ThenByDescending(p => p.Start).FirstOrDefault();
            if (hit is null)
                return new("", 0.5, pos.ToArray(), $"There is no {Months[m - 1][^1]}{(year > 0 ? " " + year : "")} period for this hospital.");
            var note = year == 0 && ordered.Count(p => p.Start.Month == m) > 1 ? $"{Months[m - 1][^1]} taken as {hit.Start.Year}" : null;
            return new(hit.Code, 1.0, pos.Order().ToArray(), note);
        }

        // Numeric: 2026 08, 08 2026.
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            int y = 0, m = 0;
            if (Year(tokens[i]) is var a and > 0 && tokens[i].Length == 4 && int.TryParse(tokens[i + 1], out var b) && b is >= 1 and <= 12) { y = a; m = b; }
            else if (int.TryParse(tokens[i], out var c) && c is >= 1 and <= 12 && tokens[i].Length <= 2 && Year(tokens[i + 1]) is var d and > 0 && tokens[i + 1].Length == 4) { y = d; m = c; }
            if (y == 0) continue;
            var hit = ordered.FirstOrDefault(p => p.Start.Year == y && p.Start.Month == m);
            return hit is null
                ? new("", 0.5, [i, i + 1], $"There is no {y}-{m:00} period for this hospital.")
                : new(hit.Code, 1.0, [i, i + 1], null);
        }

        // Relative.
        var text = " " + string.Join(' ', tokens) + " ";
        int[] At(params string[] words) =>
            Enumerable.Range(0, tokens.Length).Where(k => words.Contains(tokens[k])).ToArray();
        // "this period" is the one being looked at; "latest" is the newest with a run.
        if (text.Contains(" this month ") || text.Contains(" this period ") || text.Contains(" current period ") || text.Contains(" current month "))
        {
            var current = ordered.FirstOrDefault(p => p.Code == anchor) ?? latest;
            return new(current.Code, 1.0, At("this", "month", "period", "current"), null);
        }
        if (text.Contains(" latest ") || text.Contains(" most recent ") || text.Contains(" newest "))
            return new(latest.Code, 1.0, At("latest", "most", "recent", "newest"), null);
        if (text.Contains(" last month ") || text.Contains(" previous month ") || text.Contains(" prior month ") || text.Contains(" previous period ")
            || text.Contains(" last period ") || text.Contains(" prior period ") || text.Contains(" month before "))
        {
            var from = ordered.FindIndex(p => p.Code == anchor);
            if (from < 0) from = ordered.IndexOf(latest);
            if (from <= 0) return new("", 0.5, At("last", "previous", "prior", "month", "period", "before"), "There is no earlier period.");
            var prev = ordered[from - 1];
            return new(prev.Code, 1.0, At("last", "previous", "prior", "month", "period", "before"), $"the period before {ordered[from].Code}");
        }
        return null;
    }
}
