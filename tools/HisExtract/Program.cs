using HisExtract;

return await App.RunAsync(args);

static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("HISGENX → VERITA extractor");
        Console.WriteLine("Read-only. Writes numbered CSVs the costing app Data import screen accepts.");
        Console.WriteLine();

        var s = Parse(args);
        if (args.Any(a => a is "-h" or "--help"))
        {
            Help();
            return 0;
        }

        var command = Command(args);
        if (command is null) command = Ask("Command  probe / catalogue / facts / all", "probe");

        s.ConnectionString = Env("HIS_CONNECTION") ?? Flag(args, "--cs")
            ?? (Interactive(args) ? Ask("Connection string", s.ConnectionString) : s.ConnectionString);
        s.SetupId = Flag(args, "--setup") ?? (Interactive(args) ? Ask("SetupId", s.SetupId) : s.SetupId);
        if (short.TryParse(Flag(args, "--project") ?? (Interactive(args) ? Ask("ProjectId", s.ProjectId.ToString()) : s.ProjectId.ToString()), out var pid))
            s.ProjectId = pid;
        s.HospitalCode = Flag(args, "--hospital") ?? (Interactive(args) ? Ask("Hospital code", s.HospitalCode) : s.HospitalCode);
        s.OutDir = Flag(args, "--out") ?? s.OutDir;

        s.From = ParseDate(Flag(args, "--from")) ?? s.From;
        s.To = ParseDate(Flag(args, "--to")) ?? s.To;
        if (command is "facts" or "all")
        {
            s.From ??= Interactive(args) ? AskDate("From (yyyy-MM-dd)", PreviousMonthStart()) : PreviousMonthStart();
            s.To ??= s.From.Value.AddMonths(1);
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
        var zip = Path.Combine(s.OutDir, $"{s.HospitalCode}-catalogue.zip");
        Console.WriteLine();
        Console.WriteLine("Catalogue pack");
        using var pack = new CsvZipWriter(zip, Readme(s, "catalogue", null, null));
        await CatalogueExtract.RunAsync(db, s, pack);
    }

    private static async Task Facts(HisConnection db, Settings s)
    {
        var zip = Path.Combine(s.OutDir, $"{s.HospitalCode}-{s.From:yyyy-MM}-facts.zip");
        Console.WriteLine();
        Console.WriteLine("Facts pack");
        using var pack = new CsvZipWriter(zip, Readme(s, "facts", s.From, s.To));
        await FactsExtract.RunAsync(db, s, pack);
    }

    private static string Readme(Settings s, string pack, DateTime? from, DateTime? to) =>
        $"""
        VERITA HIS extract
        Pack: {pack}
        Hospital: {s.HospitalCode}
        HIS SetupID: {s.SetupId}
        HIS ProjectID: {s.ProjectId}
        Period: {(from is null ? "n/a" : $"{from:yyyy-MM-dd} .. {to:yyyy-MM-dd}")}
        Extracted (UTC): {DateTime.UtcNow:u}
        Server: {ServerOf(s.ConnectionString)}

        Import on Data import. Use a user whose hospitalScope includes {s.HospitalCode}
        (or *). Shared tables (Payer, Drg, ServiceLine) need hospitalScope = *.
        Validate (dry run) before Import.
        """;

    private static string? Command(string[] args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => a is "probe" or "catalogue" or "facts" or "all");

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
              HisExtract facts --from 2026-09-01 --to 2026-10-01
              HisExtract all --month 2026-09

            Options:
              --cs         SQL connection string (or env HIS_CONNECTION)
              --setup      HIS SetupID          default 010266
              --project    HIS ProjectID        default 15
              --hospital   VERITA hospital code default RYN-UAT
              --from       period start yyyy-MM-dd (facts)
              --to         period end exclusive
              --month      yyyy-MM shorthand for from/to
              --out        output directory     default ./his-extract-out

            Defaults (UAT):
              Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123

            Publish for the HIS PC:
              dotnet publish tools/HisExtract -c Release -r win-x64 --self-contained -o ./publish/HisExtract
            """);
    }
}
