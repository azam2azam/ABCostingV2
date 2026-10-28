using HisExtract;

return await App.RunAsync(args);

static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("HISGENX extractor");
        Console.WriteLine("Read-only. Identity is HIS SetupID + ProjectID. One facts zip per calendar month.");
        Console.WriteLine();

        var s = Parse(args);
        if (args.Any(a => a is "-h" or "--help"))
        {
            Help();
            return 0;
        }

        var command = Command(args);
        if (command is "selftest")
            return SelfTest();
        if (command is null) command = Ask("Command  probe / catalogue / facts / all", "probe");

        s.ConnectionString = Env("HIS_CONNECTION") ?? Flag(args, "--cs")
            ?? (Interactive(args) ? Ask("Connection string", s.ConnectionString) : s.ConnectionString);
        s.SetupId = Flag(args, "--setup") ?? (Interactive(args) ? Ask("SetupId", s.SetupId) : s.SetupId);
        if (short.TryParse(Flag(args, "--project") ?? (Interactive(args) ? Ask("ProjectId", s.ProjectId.ToString()) : s.ProjectId.ToString()), out var pid))
            s.ProjectId = pid;
        s.HospitalCode = Flag(args, "--hospital") ?? (Interactive(args) ? Ask("Hospital code", s.HospitalCode) : s.HospitalCode);
        s.OutDir = Flag(args, "--out") ?? s.OutDir;

        s.From = ParseDate(Flag(args, "--from")) ?? s.From;
        var toInclusive = ParseDate(Flag(args, "--to"));
        if (toInclusive is not null)
            s.To = toInclusive.Value.AddDays(1);
        if (command is "facts" or "all")
        {
            s.From ??= Interactive(args) ? AskDate("From (yyyy-MM-dd)", PreviousMonthStart()) : PreviousMonthStart();
            if (s.To is null)
            {
                var defaultTo = LastDayOfMonth(s.From.Value);
                var asked = Interactive(args) ? AskDate("To inclusive (yyyy-MM-dd)", defaultTo) : defaultTo;
                s.To = asked.AddDays(1);
            }
            if (s.To <= s.From)
            {
                Console.Error.WriteLine($"Empty period: from {s.From:yyyy-MM-dd} to inclusive {s.To.Value.AddDays(-1):yyyy-MM-dd}.");
                return 1;
            }
        }

        Directory.CreateDirectory(s.OutDir);
        Console.WriteLine();
        Console.WriteLine($"Connecting to {ServerOf(s.ConnectionString)}  db={DbOf(s.ConnectionString)} ...");

        try
        {
            await using var db = new HisConnection(s.ConnectionString);
            await db.OpenAsync();
            Console.WriteLine("Connected.");

            switch (command)
            {
                case "probe":
                    await Probe.RunAsync(db, s);
                    break;
                case "catalogue":
                    await Catalogue(db, s);
                    break;
                case "facts":
                    await Facts(db, s);
                    break;
                case "all":
                    await Catalogue(db, s);
                    await Facts(db, s);
                    break;
                default:
                    Console.WriteLine($"Unknown command '{command}'.");
                    Help();
                    return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);
            if (ex.InnerException is not null) Console.Error.WriteLine(ex.InnerException.Message);
            return 1;
        }
    }

    private static async Task Catalogue(HisConnection db, Settings s)
    {
        var zip = Path.Combine(s.OutDir, $"{s.PackPrefix}-catalogue.zip");
        Console.WriteLine();
        Console.WriteLine($"Catalogue pack  {s.PackPrefix}");
        using var pack = new CsvZipWriter(zip, Readme(s, "catalogue", null, null), s);
        await CatalogueExtract.RunAsync(db, s, pack);
        pack.Completed = true;
    }

    private static async Task Facts(HisConnection db, Settings s)
    {
        var slices = MonthSlices.Of(s.From!.Value, s.To!.Value);
        Console.WriteLine();
        Console.WriteLine($"Facts pack  {s.PackPrefix}  {slices.Count} month file(s)  {s.From:yyyy-MM-dd} .. {s.To.Value.AddDays(-1):yyyy-MM-dd} inclusive");
        var origFrom = s.From;
        var origTo = s.To;
        try
        {
            foreach (var slice in slices)
            {
                s.From = slice.From;
                s.To = slice.ToExclusive;
                var zip = Path.Combine(s.OutDir, $"{s.PackPrefix}-{slice.YearMonth}-facts.zip");
                Console.WriteLine();
                Console.WriteLine($"  Month {slice.YearMonth}  {slice.From:yyyy-MM-dd} .. {slice.ToExclusive.AddDays(-1):yyyy-MM-dd} inclusive");
                using var pack = new CsvZipWriter(zip, Readme(s, "facts", slice.From, slice.ToExclusive), s);
                await FactsExtract.RunAsync(db, s, pack);
                pack.Completed = true;
            }
        }
        finally
        {
            s.From = origFrom;
            s.To = origTo;
        }
    }

    private static string Readme(Settings s, string pack, DateTime? from, DateTime? toExclusive) =>
        $"""
        HIS extract (HISGENX)
        Pack: {pack}
        HIS SetupID: {s.SetupId}
        HIS ProjectID: {s.ProjectId}
        VERITA hospitalCode still stamped on rows: {s.HospitalCode}
        Period: {(from is null ? "n/a" : $"{from:yyyy-MM-dd} .. {toExclusive!.Value.AddDays(-1):yyyy-MM-dd} inclusive")}
        Extracted (UTC): {DateTime.UtcNow:u}
        Server: {ServerOf(s.ConnectionString)}

        Identity is SetupID + ProjectID (every CSV + zip name). Do not treat the zip
        filename as a VERITA hospital. Inner CSV names stay NN_Table.csv.

        These packs are not yet importable into current VERITA Data import (extra
        setupId/projectId columns + 00_manifest.csv). Copy the zips back for validation
        before VERITA import is updated.
        """;

    private static int SelfTest()
    {
        var failed = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine(ok ? $"  ok  {name}" : $"  FAIL  {name}");
            if (!ok) failed++;
        }

        var four = MonthSlices.Of(new DateTime(2026, 7, 1), new DateTime(2026, 11, 1));
        Check("Jul–Oct inclusive → 4 month files", four.Count == 4
            && four[0].YearMonth == "2026-07" && four[0].From == new DateTime(2026, 7, 1) && four[0].ToExclusive == new DateTime(2026, 8, 1)
            && four[3].YearMonth == "2026-10" && four[3].ToExclusive == new DateTime(2026, 11, 1));

        var partial = MonthSlices.Of(new DateTime(2026, 7, 15), new DateTime(2026, 9, 21));
        Check("partial first/last month stay month-by-month", partial.Count == 3
            && partial[0].YearMonth == "2026-07" && partial[0].From == new DateTime(2026, 7, 15)
            && partial[1].YearMonth == "2026-08"
            && partial[2].YearMonth == "2026-09" && partial[2].ToExclusive == new DateTime(2026, 9, 21));

        var one = MonthSlices.Of(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1));
        Check("--month 2026-09 → one zip", one.Count == 1 && one[0].YearMonth == "2026-09");

        var tmp = Path.Combine(Path.GetTempPath(), "his-extract-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var s = new Settings { SetupId = "010266", ProjectId = 15, HospitalCode = "RYN-UAT", OutDir = tmp };
        Check("zip prefix HIS-010266-15", s.PackPrefix == "HIS-010266-15");
        var zip = Path.Combine(tmp, $"{s.PackPrefix}-catalogue.zip");
        using (var pack = new CsvZipWriter(zip, "selftest", s))
        {
            using (var csv = pack.Create("01_Hospital.csv", "code", "name"))
                csv.Write("RYN-UAT", "Rayan");
            pack.WriteManifest("catalogue", null, null, s.HospitalCode);
            pack.Completed = true;
        }
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            var hospital = z.GetEntry("01_Hospital.csv");
            var manifest = z.GetEntry("00_manifest.csv");
            Check("inner names 00_manifest + 01_Hospital", hospital is not null && manifest is not null);
            using var r = new StreamReader(hospital!.Open());
            var header = r.ReadLine();
            var row = r.ReadLine();
            Check("CSV header starts setupId,projectId", header == "setupId,projectId,code,name");
            Check("CSV row stamps HIS tenant", row == "010266,15,RYN-UAT,Rayan");
            using var mr = new StreamReader(manifest!.Open());
            var mh = mr.ReadLine();
            Check("manifest has HIS + pack columns", mh is not null && mh.StartsWith("setupId,projectId,pack,"));
        }
        try { Directory.Delete(tmp, recursive: true); } catch { /* temp */ }

        Console.WriteLine(failed == 0 ? "selftest passed" : $"selftest failed ({failed})");
        return failed == 0 ? 0 : 1;
    }

    private static string? Command(string[] args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => a is "probe" or "catalogue" or "facts" or "all" or "selftest");

    private static Settings Parse(string[] args)
    {
        var s = new Settings();
        var month = Flag(args, "--month");
        if (month is not null && DateTime.TryParse(month + "-01", out var m))
        {
            s.From = m;
            s.To = m.AddMonths(1);
        }
        return s;
    }

    private static bool Interactive(string[] args) =>
        !args.Any(a => a is "probe" or "catalogue" or "facts" or "all");

    private static string Ask(string label, string def)
    {
        Console.Write($"{label} [{def}]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? def : line.Trim();
    }

    private static DateTime AskDate(string label, DateTime def)
    {
        Console.Write($"{label} [{def:yyyy-MM-dd}]: ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return def;
        return DateTime.Parse(line.Trim());
    }

    private static DateTime PreviousMonthStart()
    {
        var d = DateTime.Today.AddMonths(-1);
        return new DateTime(d.Year, d.Month, 1);
    }

    private static DateTime LastDayOfMonth(DateTime d) =>
        new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        var prefixed = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return prefixed?[(name.Length + 1)..];
    }

    private static DateTime? ParseDate(string? v) =>
        DateTime.TryParse(v, out var d) ? d.Date : null;

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string ServerOf(string cs)
    {
        foreach (var part in cs.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Server", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return "(unknown)";
    }

    private static string DbOf(string cs)
    {
        foreach (var part in cs.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Database", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return "(unknown)";
    }

    private static void Help()
    {
        Console.WriteLine("""
            Usage:
              HisExtract probe
              HisExtract catalogue
              HisExtract facts --from 2026-07-01 --to 2026-10-31
              HisExtract all --month 2026-09
              HisExtract selftest

            One facts zip per calendar month, even when --from/--to spans months:
              HIS-010266-15-catalogue.zip
              HIS-010266-15-2026-07-facts.zip
              HIS-010266-15-2026-08-facts.zip
              ...

            Options:
              --cs         SQL connection string (or env HIS_CONNECTION)
              --setup      HIS SetupID          default 010266
              --project    HIS ProjectID        default 15
              --hospital   VERITA hospital code still stamped on row codes (next slice removes this)
              --from       period start yyyy-MM-dd (facts)
              --to         period end inclusive yyyy-MM-dd
              --month      yyyy-MM shorthand for one full month
              --out        output directory     default ./his-extract-out

            Defaults (UAT):
              Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123

            Publish for the HIS PC:
              dotnet publish tools/HisExtract -c Release -r win-x64 --self-contained -o ./publish/HisExtract
            """);
    }
}
