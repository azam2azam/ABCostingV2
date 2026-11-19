using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Engine;
using Verita.Infrastructure;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Fixture;
using Verita.Infrastructure.Governance;
using Verita.Infrastructure.Runs;

namespace Verita.Tools;

/// <summary>
/// Operator tooling. `bootstrap` takes an empty SQL Server to a costed, reconciled platform:
///   migrate --reset   empty the database and build it from deploy/iis/sql/01-schema.sql (there are no EF migrations)
///   import            load the reference fixture (fixture/source) through SqlBulkCopy
///   cost              create and execute a run for every reference hospital-period
///   scenarios         re-run the six acceptance scenarios (FR-8, BRD 13)
///   parity            compare against the reference engine's results (BRD 23.4)
/// </summary>
public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var cmd = args.FirstOrDefault() ?? "help";
        var root = FindRoot();
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Verita")
                 ?? "Server=localhost\\SQLEXPRESS;Database=Verita;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";
        var demoPassword = Environment.GetEnvironmentVariable("VERITA_DEMO_PASSWORD") ?? "Demo!Pass123";
        var fixture = Path.Combine(root, "fixture");

        var services = new ServiceCollection()
            .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning))
            .AddVerita(cs)
            .BuildServiceProvider();

        try
        {
            switch (cmd)
            {
                case "migrate": await Migrate(root, cs, args.Contains("--reset")); break;
                case "import": await Import(services, fixture, demoPassword); break;
                case "cost": await Cost(services, fixture); break;
                case "scenarios": await Scenarios(services, fixture); break;
                case "parity": await Parity.RunAsync(services, fixture, Path.Combine(root, "docs", "PARITY.md")); break;
                case "bootstrap":
                    await Migrate(root, cs, reset: true);
                    await Import(services, fixture, demoPassword);
                    await Cost(services, fixture);
                    await Scenarios(services, fixture);
                    await Parity.RunAsync(services, fixture, Path.Combine(root, "docs", "PARITY.md"));
                    break;
                default:
                    Console.WriteLine("usage: verita-tools migrate [--reset] | import | cost | scenarios | parity | bootstrap");
                    return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            if (ex is AppError { Detail: not null } ae) Console.Error.WriteLine(JsonSerializer.Serialize(ae.Detail));
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        foreach (var start in new[] { cwd, dir })
            for (var d = start; d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Verita.sln"))) return d.FullName;
        throw new InvalidOperationException("Run from inside the repository (Verita.sln not found).");
    }

    /// <summary>
    /// Builds the schema from the same scripts a server uses (deploy/iis/sql). Creates the database when it does not
    /// exist (local development: that needs server rights). With --reset, empties it first with 00-drop-all.sql.
    /// </summary>
    private static async Task Migrate(string root, string cs, bool reset)
    {
        var dir = Path.Combine(root, "deploy", "iis", "sql");
        var name = new SqlConnectionStringBuilder(cs).InitialCatalog;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("The connection string names no database.");

        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"IF DB_ID(@n) IS NULL CREATE DATABASE [{name.Replace("]", "]]")}];";
            create.Parameters.AddWithValue("@n", name);
            await create.ExecuteNonQueryAsync();
        }

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        conn.InfoMessage += (_, e) => Console.WriteLine("  " + e.Message);
        if (reset)
        {
            Console.WriteLine($"Emptying {name} (00-drop-all.sql)…");
            var drop = (await File.ReadAllTextAsync(Path.Combine(dir, "00-drop-all.sql")))
                .Replace("DECLARE @confirm sysname = N'';", $"DECLARE @confirm sysname = N'{name.Replace("'", "''")}';");
            await RunScriptAsync(conn, drop);
        }
        await using (var any = conn.CreateCommand())
        {
            any.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
            if (Convert.ToInt32(await any.ExecuteScalarAsync()) > 0)
            {
                Console.WriteLine($"{name} already has tables; left as it is. Use migrate --reset to rebuild it.");
                return;
            }
        }
        Console.WriteLine("Creating the schema (01-schema.sql)…");
        await RunScriptAsync(conn, await File.ReadAllTextAsync(Path.Combine(dir, "01-schema.sql")));
        Console.WriteLine("Schema created.");
    }

    /// <summary>Runs a SQL script the way sqlcmd does: batch by batch at each GO line, stopping at the first error.</summary>
    private static async Task RunScriptAsync(SqlConnection conn, string script)
    {
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task Import(IServiceProvider sp, string fixture, string demoPassword)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContextAsync();
        Console.WriteLine("Importing reference fixture…");
        var sw = Stopwatch.StartNew();
        var loaded = await new FixtureImporter(db, demoPassword).ImportAsync(fixture, Console.WriteLine);
        Console.WriteLine($"Imported {loaded.Sum(l => l.Rows):N0} rows across {loaded.Count} tables in {sw.Elapsed.TotalSeconds:N1}s; constraints re-validated.");
    }

    private sealed record RefRun(string Id, string Code, string Name, string Status, string HospitalId, string PeriodId, string TriggeredBy);

    private static List<RefRun> ReferenceRuns(string fixture) =>
        FixtureImporter.ReadNdjson(Path.Combine(fixture, "reference", "CalculationRun.ndjson.gz"))
            .Select(e => new RefRun(e.GetProperty("id").GetString()!, e.GetProperty("code").GetString()!, e.GetProperty("name").GetString()!,
                e.GetProperty("status").GetString()!, e.GetProperty("hospitalId").GetString()!, e.GetProperty("periodId").GetString()!,
                e.GetProperty("triggeredBy").GetString()!))
            .OrderBy(r => r.Code).ToList();

    private static async Task<Principal> PrincipalFor(IServiceProvider sp, string email)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContextAsync();
        var u = await db.Users.FirstAsync(x => x.Email == email);
        return new Principal(u.Id, u.Email, u.Name, u.Role, u.Title, Principal.ParseScope(u.HospitalScope), Principal.ParseScope(u.DeptScope));
    }

    private static async Task Cost(IServiceProvider sp, string fixture)
    {
        using var scope = sp.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        var governance = scope.ServiceProvider.GetRequiredService<GovernanceService>();
        var manager = await PrincipalFor(sp, "costing.manager@verita.health");
        var cfo = await PrincipalFor(sp, "cfo@verita.health");

        foreach (var r in ReferenceRuns(fixture))
        {
            await using var lookup = await sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContextAsync();
            var run = await lookup.CalculationRuns.FirstOrDefaultAsync(x => x.Id == FixtureImporter.MapId(r.Id))
                      ?? await runs.CreateAsync(FixtureImporter.MapId(r.HospitalId), FixtureImporter.MapId(r.PeriodId), r.TriggeredBy, r.Name,
                          id: FixtureImporter.MapId(r.Id), code: r.Code);
            var sw = Stopwatch.StartNew();
            var exec = await runs.ExecuteAsync(run.Id, force: false, r.TriggeredBy);
            var forced = false;
            if (exec.Status == "FAILED" && exec.Error?.Contains("data-quality error") == true)
            {
                // ERROR issues (for example DQ-GL-UNMAPPED) allow a forced run that can never publish (FR-9.8).
                exec = await runs.ExecuteAsync(run.Id, force: true, r.TriggeredBy);
                forced = true;
            }
            var m = exec.Metrics;
            Console.WriteLine($"{r.Code,-24} {exec.Status,-10} {sw.Elapsed.TotalSeconds,5:N1}s  GL {m.GetValueOrDefault("glTotal"),14:N2}  patients {m.GetValueOrDefault("patientCost"),14:N2}  unexplained {m.GetValueOrDefault("unexplained"),8:N2}{(forced ? "  (forced past ERROR issues)" : "")}");
            if (exec.Error is not null) Console.WriteLine($"   {exec.Error}");

            // Take the reference's published runs through the four-eye workflow. A run blocked by
            // the BRD's publication gates stays RECONCILED and says why.
            if (r.Status is "PUBLISHED" or "REVIEWED" && exec.Status == "RECONCILED")
            {
                try
                {
                    var req = await governance.RequestAsync("CALCULATION_RUN", run.Id, "Month-end close reconciled; requesting finance approval to publish.", manager);
                    if (r.Status == "PUBLISHED")
                    {
                        await governance.DecideAsync(req.Id, true, "Reconciliation reviewed against the trial balance.", cfo);
                        await runs.TransitionAsync(run.Id, "PUBLISHED", cfo);
                    }
                    Console.WriteLine($"   lifecycle: {(r.Status == "PUBLISHED" ? "approved and published" : "awaiting finance approval")}");
                }
                catch (AppError ex)
                {
                    var reasons = ex.Detail is null ? "" : " " + JsonSerializer.Serialize(ex.Detail);
                    Console.WriteLine($"   lifecycle: stays RECONCILED — {ex.Message}{reasons}");
                }
            }
        }
    }

    private static async Task Scenarios(IServiceProvider sp, string fixture)
    {
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScenarioService>();
        var levers = FixtureImporter.ReadNdjson(Path.Combine(fixture, "source", "ScenarioLever.ndjson.gz")).ToLookup(l => l.GetProperty("scenarioId").GetString()!);
        foreach (var s in FixtureImporter.ReadNdjson(Path.Combine(fixture, "source", "Scenario.ndjson.gz")))
        {
            var id = s.GetProperty("id").GetString()!;
            var ls = levers[id].Select(l =>
            {
                var target = l.GetProperty("targetId").ValueKind == JsonValueKind.Null ? null : l.GetProperty("targetId").GetString();
                if (target is not null && FixtureImporter.LooksLikeCuid(target)) target = FixtureImporter.MapId(target).ToString();
                return new LeverInput(l.GetProperty("leverType").GetString()!, l.GetProperty("targetType").GetString()!, target,
                    l.GetProperty("targetLabel").GetString()!, l.GetProperty("changeType").GetString()!, l.GetProperty("value").GetDecimal());
            }).ToList();
            var scenario = await svc.CreateAsync(FixtureImporter.MapId(s.GetProperty("baseRunId").GetString()!), s.GetProperty("code").GetString()!,
                s.GetProperty("name").GetString()!, s.GetProperty("description").GetString()!, ls, s.GetProperty("createdBy").GetString()!, FixtureImporter.MapId(id));
            var sw = Stopwatch.StartNew();
            var rows = await svc.RunAsync(scenario.Id, scenario.CreatedBy);
            var total = rows.First(r => r.Dimension == "TOTAL" && r.DimensionId is null);
            var full = rows.First(r => r.DimensionId == "FULL_COST");
            Console.WriteLine($"{scenario.Code,-22} {sw.Elapsed.TotalSeconds,5:N1}s  patient cost {total.CostDelta,12:N2}  margin {total.MarginDelta,12:N2}  full cost {full.CostDelta,12:N2}");
        }
    }
}
