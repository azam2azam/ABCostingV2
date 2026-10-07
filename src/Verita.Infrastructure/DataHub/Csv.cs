using System.Text;

namespace Verita.Infrastructure.DataHub;

/// <summary>
/// RFC 4180 CSV, as Excel saves it ("CSV UTF-8"): quoted fields, doubled quotes, embedded commas
/// and line breaks, an optional byte-order mark. Lines whose first cell starts with # are guidance
/// rows from the template and are skipped. Each row carries its line number as "__row".
/// </summary>
public static class Csv
{
    public static string Escape(string? v)
    {
        v ??= "";
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static List<Dictionary<string, string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var records = new List<(int Line, List<string> Cells)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var startLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else { if (c == '\n') line++; cell.Append(c); }
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': cells.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    cells.Add(cell.ToString()); cell.Clear();
                    records.Add((startLine, cells)); cells = []; line++; startLine = line;
                    break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); records.Add((startLine, cells)); }

        var nonEmpty = records.Where(r => r.Cells.Any(x => x.Trim().Length > 0)).ToList();
        if (nonEmpty.Count == 0) return [];
        var header = nonEmpty[0].Cells.Select(h => h.Trim()).ToArray();
        var rows = new List<Dictionary<string, string>>();
        foreach (var (ln, rec) in nonEmpty.Skip(1))
        {
            if (rec.Count > 0 && rec[0].TrimStart().StartsWith('#')) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["__row"] = ln.ToString() };
            for (var i = 0; i < header.Length; i++)
                if (header[i].Length > 0) row[header[i]] = i < rec.Count ? rec[i] : "";
            rows.Add(row);
        }
        return rows;
    }
}
