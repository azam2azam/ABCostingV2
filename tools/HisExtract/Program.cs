using HisExtract;

return await App.RunAsync(args);

static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("HISGENX extractor");
        Console.WriteLine("Read-only. Identity is HIS SetupID + ProjectID. One facts zip per day.");
        Console.WriteLine();

        var s = new Settings();
        var monthRaw = Flag(args, "--month");
        if (monthRaw is not null)
        {
            if (!Dates.TryMonth(monthRaw, out var monthStart))
            {
                Console.Error.WriteLine($"Could not parse --month '{monthRaw}'. Use yyyy-MM.");
                return 1;
            }
            s.From = monthStart;
            s.To = monthStart.AddMonths(1);
        }
        if (args.Any(a => a is "-h" or "--help"))
        {
            Help();
            return 0;
        }

        var command = Command(args);
        if (command is "selftest")
            return SelfTest();
        if (command is null) command = Ask("Command  probe / catalogue / facts / all / batchcheck", "probe");

        s.ConnectionString = Env("HIS_CONNECTION") ?? Flag(args, "--cs")
            ?? (Interactive(args) ? AskSecret("Connection string", s.ConnectionString) : s.ConnectionString);
        s.SetupId = Flag(args, "--setup") ?? (Interactive(args) ? Ask("SetupId", s.SetupId) : s.SetupId);
        if (short.TryParse(Flag(args, "--project") ?? (Interactive(args) ? Ask("ProjectId", s.ProjectId.ToString()) : s.ProjectId.ToString()), out var pid))
            s.ProjectId = pid;
        if (Flag(args, "--hospital") is not null)
            Console.WriteLine("Note: --hospital is ignored. Identity is SetupID + ProjectID. Choose the VERITA hospital on import.");
        s.OutDir = Flag(args, "--out") ?? s.OutDir;
        s.CcMapPath = Flag(args, "--cc-map");
        if (Flag(args, "--pharmacy-db") is { } phrDb)
        {
            if (phrDb.Length > 0 && !phrDb.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            {
                Console.Error.WriteLine($"--pharmacy-db '{phrDb}' must be a database name (letters, digits, _), or empty to skip item names.");
                return 1;
            }
            s.PharmacyDb = phrDb;
        }
        s.Claims = args.Any(a => string.Equals(a, "--claims", StringComparison.OrdinalIgnoreCase));
        if (Flag(args, "--query-timeout") is { } qt)
        {
            if (!int.TryParse(qt, out var qts) || qts < 0)
            {
                Console.Error.WriteLine($"Could not parse --query-timeout '{qt}'. Use seconds, e.g. 1800 (0 = no limit).");
                return 1;
            }
            s.QueryTimeoutSeconds = qts;
        }
        if (Flag(args, "--ward-nurse-min") is { } wn)
        {
            if (!decimal.TryParse(wn, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var wardMin) || wardMin < 0)
            {
                Console.Error.WriteLine($"Could not parse --ward-nurse-min '{wn}'. Use minutes per patient-day, e.g. 260.");
                return 1;
            }
            s.WardNurseMinutesPerDay = wardMin;
        }

        var fromRaw = Flag(args, "--from");
        var toRaw = Flag(args, "--to");
        if (fromRaw is not null)
        {
            s.From = Dates.TryDay(fromRaw) ?? FailDate("--from", fromRaw);
            if (s.From is null) return 1;
            if (toRaw is null) s.To = null;
        }
        if (toRaw is not null)
        {
            var toInclusive = Dates.TryDay(toRaw, endOfMonthIfYearMonth: true);
            if (toInclusive is null) { FailDate("--to", toRaw); return 1; }
            s.To = Dates.ExclusiveEnd(toInclusive.Value);
        }
        var batchSize = 100;
        if (Flag(args, "--batch-size") is { } bs && (!int.TryParse(bs, out batchSize) || batchSize < 1))
        {
            Console.Error.WriteLine($"Could not parse --batch-size '{bs}'. Use a number of encounters, e.g. 100.");
            return 1;
        }
        if (command is "facts" or "all" or "probe" or "batchcheck")
        {
            var needPeriod = command is "facts" or "all" or "batchcheck" || fromRaw is not null || toRaw is not null || s.From is not null;
            if (needPeriod)
            {
                s.From ??= AskDate("From (yyyy-MM-dd)", PreviousMonthStart());
                if (s.To is null)
                {
                    var asked = AskDate("To inclusive (yyyy-MM-dd)", DateTime.Today, asEnd: true);
                    s.To = Dates.ExclusiveEnd(asked);
                }
                if (s.To <= s.From)
                {
                    Console.Error.WriteLine($"Empty period: from {s.From:yyyy-MM-dd} to inclusive {s.To.Value.AddDays(-1):yyyy-MM-dd}.");
                    return 1;
                }
                Console.WriteLine($"Period {s.From:yyyy-MM-dd} .. {s.To.Value.AddDays(-1):yyyy-MM-dd} inclusive ({DaySlices.Of(s.From.Value, s.To.Value).Count} daily file(s) for facts).");
            }
        }

        Directory.CreateDirectory(s.OutDir);
        Console.WriteLine();
        Console.WriteLine("Connection string  ****");
        Console.WriteLine("Connecting ...");

        try
        {
            await using var db = new HisConnection(s.ConnectionString) { QueryTimeoutSeconds = s.QueryTimeoutSeconds };
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
                case "batchcheck":
                    return await BatchCheck.RunAsync(db, s, batchSize) ? 0 : 2;
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
        try
        {
            await CatalogueExtract.RunAsync(db, s, pack);
        }
        catch (Exception ex)
        {
            pack.Fail(ex);
            throw;
        }
        pack.Completed = true;
    }

    private static async Task Facts(HisConnection db, Settings s)
    {
        var slices = DaySlices.Of(s.From!.Value, s.To!.Value);
        Console.WriteLine();
        Console.WriteLine($"Facts pack  {s.PackPrefix}  {slices.Count} daily file(s)  {s.From:yyyy-MM-dd} .. {s.To.Value.AddDays(-1):yyyy-MM-dd} inclusive");
        var origFrom = s.From;
        var origTo = s.To;
        try
        {
            foreach (var slice in slices)
            {
                s.From = slice.From;
                s.To = slice.ToExclusive;
                var zip = Path.Combine(s.OutDir, $"{s.PackPrefix}-{slice.Day}-facts.zip");
                Console.WriteLine();
                Console.WriteLine($"  Day {slice.Day}");
                using var pack = new CsvZipWriter(zip, Readme(s, "facts", slice.From, slice.ToExclusive), s);
                try
                {
                    await FactsExtract.RunAsync(db, s, pack);
                }
                catch (Exception ex)
                {
                    pack.Fail(ex);
                    throw;
                }
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
        Period: {(from is null ? "n/a" : $"{from:yyyy-MM-dd} .. {toExclusive!.Value.AddDays(-1):yyyy-MM-dd} inclusive")}
        Extracted (UTC): {DateTime.UtcNow:u}

        Identity is SetupID + ProjectID (every CSV + zip name). Do not treat the zip
        filename as a VERITA hospital. Inner CSV names stay NN_Table.csv.

        Facts are one zip per day. Utilization is raw/measured — not invented Activity×Resource:
        39_ConsumptionItem.csv (item quantities), 40_StaffTime.csv (OR/OP/ER staff minutes),
        41_EncounterTransfer.csv (INP_Transfer timing as stored). 35_ActivityTransaction.csv
        is omitted (no ACT-/RES- invent from HIS clocks). Invoice price is not cost. Each day
        carries every invoice (incl. claimed/settled/rejected), item and minute of the
        encounters admitted or invoiced that day — encounter-driven, not only rows dated in
        the window (ER appointment links folded into the day's appointment set so claims
        still follow). Rows are keyed by sourceRef. Import remaps SetupID-ProjectID onto VERITA.
        """;

    private static int SelfTest()
    {
        var failed = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine(ok ? $"  ok  {name}" : $"  FAIL  {name}");
            if (!ok) failed++;
        }

        Check("yyyy-MM-dd from", Dates.TryDay("2026-07-01") == new DateTime(2026, 7, 1));
        Check("yyyy-MM from = first of month", Dates.TryDay("2026-07") == new DateTime(2026, 7, 1));
        Check("yyyy-MM to = last of month", Dates.TryDay("2026-10", endOfMonthIfYearMonth: true) == new DateTime(2026, 10, 31));
        Check("--to 2026-10-31 → exclusive 2026-11-01", Dates.ExclusiveEnd(new DateTime(2026, 10, 31)) == new DateTime(2026, 11, 1));
        Check("Jul–Oct via --from/--to yyyy-MM", MonthSlices.Of(Dates.TryDay("2026-07")!.Value, Dates.ExclusiveEnd(Dates.TryDay("2026-10", true)!.Value)).Count == 4);

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
        Check("--month 2026-09 → one month", one.Count == 1 && one[0].YearMonth == "2026-09");
        var days = DaySlices.Of(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1));
        Check("--month 2026-09 → 30 daily zips", days.Count == 30 && days[0].Day == "2026-09-01" && days[29].Day == "2026-09-30"
            && days[29].ToExclusive == new DateTime(2026, 10, 1));
        var span = DaySlices.Of(new DateTime(2026, 7, 30), new DateTime(2026, 8, 2));
        Check("days cross a month boundary", span.Count == 3 && span[2].Day == "2026-08-01");

        var day = new DateTime(2026, 8, 3);
        Check("clock minutes 08:00-09:30", Maps.ClockMinutes(Maps.Combine(day, "08:00"), Maps.Combine(day, "09:30")) == 90);
        Check("clock minutes skip empty span", Maps.ClockMinutes(day, day) is null);
        Check("clock minutes overnight", Maps.ClockMinutes(Maps.Combine(day, "22:00"), Maps.Combine(day, "02:00")) == 240);
        Check("HIS ArrivalTime HHMMSS", Maps.Combine(day, "143000") == day.Date.AddHours(14).AddMinutes(30));
        Check("transfer duration hours", Maps.TransferMinutes(2, day, "", "") == 120);
        Check("transfer duration minutes", Maps.TransferMinutes(180, day, "", "") == 180);

        var tmp = Path.Combine(Path.GetTempPath(), "his-extract-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var s = new Settings { SetupId = "010266", ProjectId = 15, OutDir = tmp };
        Check("zip prefix HIS-010266-15", s.PackPrefix == "HIS-010266-15");
        Check("row codes use HIS tenant, not VERITA hospital", s.H == "010266-15");
        var zip = Path.Combine(tmp, $"{s.PackPrefix}-catalogue.zip");
        using (var pack = new CsvZipWriter(zip, "selftest", s))
        {
            using (var csv = pack.Create("01_Hospital.csv", "code", "name"))
                csv.Write(s.H, "Rayan");
            pack.WriteManifest("catalogue", null, null);
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
            Check("CSV row stamps HIS tenant", row == "010266,15,010266-15,Rayan");
            using var mr = new StreamReader(manifest!.Open());
            var mh = mr.ReadLine();
            Check("manifest has HIS + pack columns", mh is not null && mh.StartsWith("setupId,projectId,pack,"));
        }

        // Progress log: removed when the pack zips, kept (with the error) when it fails.
        var okZip = Path.Combine(tmp, $"{s.PackPrefix}-2026-09-01-facts.zip");
        using (var pack = new CsvZipWriter(okZip, "selftest-log", s))
        {
            pack.Step("Step one");
            pack.Completed = true;
        }
        using (var z = System.IO.Compression.ZipFile.OpenRead(okZip))
            Check("finished pack: no progress log in the zip", z.GetEntry(CsvZipWriter.ProgressLogName) is null);
        Check("finished pack: progress log deleted from the folder", !File.Exists(Path.Combine(tmp, $"{s.PackPrefix}-2026-09-01-facts", CsvZipWriter.ProgressLogName)));
        var badZip = Path.Combine(tmp, $"{s.PackPrefix}-2026-09-02-facts.zip");
        using (var pack = new CsvZipWriter(badZip, "selftest-log", s))
        {
            pack.Step("Floor consumption qty");
            pack.Fail(new TimeoutException("simulated hang"));
        }
        var badLog = Path.Combine(tmp, $"{s.PackPrefix}-2026-09-02-facts", CsvZipWriter.ProgressLogName);
        var badText = File.Exists(badLog) ? File.ReadAllText(badLog) : "";
        Check("failed pack: progress log kept with the step and the error",
            !File.Exists(badZip) && badText.Contains("START Floor consumption qty") && badText.Contains("ERROR") && badText.Contains("simulated hang"));

        var prevPage = CsvZipWriter.PageRows;
        CsvZipWriter.PageRows = 2;
        var pagedZip = Path.Combine(tmp, $"{s.PackPrefix}-paged.zip");
        try
        {
            using (var pack = new CsvZipWriter(pagedZip, "selftest-pages", s))
            {
                using (var csv = pack.Create("34_EncounterService.csv", "sourceRef"))
                {
                    csv.Write("a");
                    csv.Write("b");
                    csv.Write("c");
                    csv.Write("d");
                    csv.Write("e");
                }
                pack.Completed = true;
            }
            using var z = System.IO.Compression.ZipFile.OpenRead(pagedZip);
            var names = z.Entries.Select(e => e.Name).OrderBy(x => x).ToArray();
            Check("paging writes p2 and p3, not one overwritten file",
                names.Contains("34_EncounterService.csv")
                && names.Contains("34_EncounterService_p2.csv")
                && names.Contains("34_EncounterService_p3.csv"));
            static int DataRows(System.IO.Compression.ZipArchive zip, string name)
            {
                using var r = new StreamReader(zip.GetEntry(name)!.Open());
                return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
            }
            Check("page 1 keeps first 2 rows", DataRows(z, "34_EncounterService.csv") == 2);
            Check("page 2 keeps next 2 rows", DataRows(z, "34_EncounterService_p2.csv") == 2);
            Check("page 3 keeps last row", DataRows(z, "34_EncounterService_p3.csv") == 1);
        }
        finally { CsvZipWriter.PageRows = prevPage; }

        try { Directory.Delete(tmp, recursive: true); } catch { /* temp */ }

        Console.WriteLine(failed == 0 ? "selftest passed" : $"selftest failed ({failed})");
        return failed == 0 ? 0 : 1;
    }

    private static string? Command(string[] args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => a is "probe" or "catalogue" or "facts" or "all" or "batchcheck" or "selftest");

    private static bool Interactive(string[] args) =>
        !args.Any(a => a is "probe" or "catalogue" or "facts" or "all" or "batchcheck");

    /// <summary>Never echoes the current value: consoles get copied into logs.</summary>
    private static string AskSecret(string label, string def)
    {
        Console.Write($"{label} [****]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? def : line.Trim();
    }

    private static string Ask(string label, string def)
    {
        Console.Write($"{label} [{def}]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? def : line.Trim();
    }

    private static DateTime AskDate(string label, DateTime def, bool asEnd = false)
    {
        while (true)
        {
            Console.Write($"{label} [{def:yyyy-MM-dd}]: ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return def.Date;
            var d = Dates.TryDay(line, endOfMonthIfYearMonth: asEnd);
            if (d is not null) return d.Value;
            Console.WriteLine("  Use yyyy-MM-dd, or yyyy-MM for a whole month.");
        }
    }

    private static DateTime? FailDate(string flag, string raw)
    {
        Console.Error.WriteLine($"Could not parse {flag} '{raw}'. Use yyyy-MM-dd or yyyy-MM.");
        return null;
    }

    private static DateTime PreviousMonthStart()
    {
        var d = DateTime.Today.AddMonths(-1);
        return new DateTime(d.Year, d.Month, 1);
    }

    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        var prefixed = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return prefixed?[(name.Length + 1)..];
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static void Help()
    {
        Console.WriteLine("""
            Usage:
              HisExtract probe
              HisExtract catalogue
              HisExtract facts --from 2026-07-01 --to 2026-10-31
              HisExtract facts --from 2026-07 --to 2026-10
              HisExtract all --month 2026-09
              HisExtract selftest
              HisExtract batchcheck --from 2026-09-01 --to 2026-09-01 [--batch-size 100]
                  Extract each day whole and as the sync worker does (encounter batches, then the
                  day's refunds) and compare every table row for row. Exit code 2 on a difference.

            One facts zip per day, even when --from/--to spans days or months:
              HIS-010266-15-catalogue.zip
              HIS-010266-15-2026-07-01-facts.zip
              HIS-010266-15-2026-07-02-facts.zip
              ...

            Facts: encounter-driven billing (claims on every invoice of pack encounters),
            39_ConsumptionItem, 40_StaffTime, 41_EncounterTransfer (raw/measured).
            35_ActivityTransaction is omitted — no invented ACT-/RES- from HIS clocks.
            Quantities only: invoice / floor UnitPrice is never written as cost.
            Tables over 50,000 rows split to NN_Entity.csv, NN_Entity_p2.csv, … — import
            concatenates every page. Queries use NOLOCK / READ UNCOMMITTED.

            Options:
              --cs         SQL connection string (or env HIS_CONNECTION)
              --setup      HIS SetupID          default 010266
              --project    HIS ProjectID        default 15
              --from       period start yyyy-MM-dd or yyyy-MM (facts)
              --to         period end inclusive yyyy-MM-dd or yyyy-MM
              --month      yyyy-MM shorthand for one full month
              --out        output directory     default ./his-extract-out
              --cc-map     reviewed CSV (costCentreCode, departmentCode) that overrides
                           the derived cost centre → department link (catalogue)
              --ward-nurse-min  ward nursing minutes per patient-day   default 260
              --pharmacy-db     pharmacy database with the item master (PHR_Item, PHR_Medicine),
                                for item names and medicine prices; "" to skip   default PHARMAKON
              --claims          also extract claim, settlement and NPHIES lines (42–45);
                                without it, claimed is hisIsClaimed on revenue lines
              --query-timeout   seconds one HIS query may run, 0 = no limit   default 1800
                                (a step that times out is skipped and noted in extract-log.txt)

            Defaults (UAT):
              Connection string  ****

            Publish for the HIS PC:
              dotnet publish tools/HisExtract -c Release -r win-x64 --self-contained -o ./publish/HisExtract
            """);
    }
}
