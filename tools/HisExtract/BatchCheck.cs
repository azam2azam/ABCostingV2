using System.IO.Compression;

namespace HisExtract;

/// <summary>
/// Proves the sync worker's batches add up to the whole-day pack: for each day, extract it whole and again as the
/// worker does (the day's list, then batches, then the day's refunds), and compare every table row for row.
/// Read-only on HIS; writes packs under OutDir/batchcheck.
/// </summary>
public static class BatchCheck
{
    public static async Task<bool> RunAsync(HisConnection db, Settings s, int size)
    {
        var allOk = true;
        var dir = Path.Combine(s.OutDir, "batchcheck");
        var (from, to) = (s.From!.Value, s.To!.Value);
        try
        {
            foreach (var day in DaySlices.Of(from, to))
            {
                s.From = day.From;
                s.To = day.ToExclusive;
                Console.WriteLine();
                Console.WriteLine($"  Day {day.Day}");

                s.Batch = null;
                s.Part = FactsPart.Day;
                var whole = await PackAsync(db, s, Path.Combine(dir, $"{s.PackPrefix}-{day.Day}-day.zip"));

                var manifest = await FactsManifest.BuildAsync(db, s);
                var batches = manifest.Batches(size).GroupBy(x => x.Batch).OrderBy(g => g.Key).ToList();
                Console.WriteLine($"    {manifest.Count} encounters in {batches.Count} batch(es) of up to {size}");
                var parts = new List<string>();
                foreach (var b in batches)
                {
                    s.Batch = manifest.BatchOf(b.Select(x => x.Item));
                    s.Part = FactsPart.Batch;
                    parts.Add(await PackAsync(db, s, Path.Combine(dir, $"{s.PackPrefix}-{day.Day}-b{b.Key:000}.zip")));
                }
                s.Batch = new FactBatch([], [], [], [.. manifest.ErLinks]);
                s.Part = FactsPart.DayRefunds;
                parts.Add(await PackAsync(db, s, Path.Combine(dir, $"{s.PackPrefix}-{day.Day}-refunds.zip")));

                allOk &= Compare(Rows(whole), parts.Select(Rows).ToList());
            }
        }
        finally
        {
            s.From = from;
            s.To = to;
            s.Batch = null;
            s.Part = FactsPart.Day;
        }
        Console.WriteLine();
        Console.WriteLine(allOk ? "batchcheck: batches match the whole-day packs." : "batchcheck: DIFFERENCES found (see above).");
        return allOk;
    }

    private static async Task<string> PackAsync(HisConnection db, Settings s, string zip)
    {
        using var pack = new CsvZipWriter(zip, $"batchcheck {s.Part}", s);
        try { await FactsExtract.RunAsync(db, s, pack); }
        catch (Exception ex) { pack.Fail(ex); throw; }
        foreach (var w in pack.Warnings) Console.WriteLine($"    WARN {w}");
        pack.Completed = true;
        return zip;
    }

    /// <summary>Data rows per table (pages joined), without the manifest.</summary>
    private static Dictionary<string, HashSet<string>> Rows(string zip)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var z = ZipFile.OpenRead(zip);
        foreach (var e in z.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(e.Name);
            var p = name.LastIndexOf("_p", StringComparison.Ordinal);
            if (p > 0 && int.TryParse(name[(p + 2)..], out _)) name = name[..p];
            if (name.StartsWith("00_", StringComparison.Ordinal)) continue;
            if (!tables.TryGetValue(name, out var set)) tables[name] = set = new HashSet<string>(StringComparer.Ordinal);
            using var r = new StreamReader(e.Open());
            r.ReadLine();
            while (r.ReadLine() is { } line) if (line.Length > 0) set.Add(line);
        }
        return tables;
    }

    private static bool Compare(Dictionary<string, HashSet<string>> whole, List<Dictionary<string, HashSet<string>>> parts)
    {
        var ok = true;
        foreach (var table in whole.Keys.Concat(parts.SelectMany(p => p.Keys)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            var day = whole.GetValueOrDefault(table) ?? [];
            var batched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in parts) if (p.TryGetValue(table, out var set)) batched.UnionWith(set);
            var missing = day.Where(l => !batched.Contains(l)).ToList();
            var extra = batched.Where(l => !day.Contains(l)).ToList();
            var same = missing.Count == 0 && extra.Count == 0;
            ok &= same;
            Console.WriteLine($"    {(same ? "ok  " : "DIFF")} {table,-26} day {day.Count,8:N0}  batched {batched.Count,8:N0}{(same ? "" : $"  missing {missing.Count:N0}  extra {extra.Count:N0}")}");
            foreach (var l in missing.Take(3)) Console.WriteLine($"           - {Short(l)}");
            foreach (var l in extra.Take(3)) Console.WriteLine($"           + {Short(l)}");
        }
        return ok;
    }

    private static string Short(string line) => line.Length > 160 ? line[..160] + "…" : line;
}
