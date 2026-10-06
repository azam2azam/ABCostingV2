using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Fixture;

namespace Verita.Tools;

/// <summary>
/// Parity verification against the reference implementation (BRD 23.4). The comparison runs in
/// order of diagnostic value: run totals, cost-centre balances, resource rates and capacity,
/// procedure standard unit cost, encounter totals. Differences beyond tolerance are listed, not
/// hidden; the tolerance is never widened to make a comparison pass.
/// </summary>
public static class Parity
{
    public sealed record Check(string Granularity, string Tolerance, int Rows, int Beyond, decimal MaxDiff, List<string> Worst);

    private static decimal D(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
    private static string S(JsonElement e, string prop) => e.GetProperty(prop).GetString()!;
    private static Guid G(JsonElement e, string prop) => FixtureImporter.MapId(S(e, prop));

    public static async Task RunAsync(IServiceProvider sp, string fixture, string reportPath)
    {
        // Pass 1 reproduces the reference's halala rounding path (distributions at two places), so
        // any remaining difference is a defect rather than precision. Pass 2 is the rebuild as it
        // runs in production, at the four-place storage scale.
        Console.WriteLine("Parity pass 1 — reference rounding path (distribution at 2 dp):");
        await Recost(sp, fixture, 2);
        var referencePath = await CompareAll(sp, fixture);
        Console.WriteLine("Parity pass 2 — production rounding (distribution at 4 dp):");
        await Recost(sp, fixture, 4);
        var production = await CompareAll(sp, fixture);
        await Write(reportPath, referencePath, production);
    }

    private static async Task Recost(IServiceProvider sp, string fixture, int scale)
    {
        using var scope = sp.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<Verita.Infrastructure.Runs.RunService>();
        foreach (var r in FixtureImporter.ReadNdjson(Path.Combine(fixture, "reference", "CalculationRun.ndjson.gz")))
        {
            var id = G(r, "id");
            var exec = await runs.ExecuteAsync(id, force: false, "parity", distributionScale: scale);
            if (exec.Status == "FAILED") exec = await runs.ExecuteAsync(id, force: true, "parity", distributionScale: scale);
            if (exec.Status != "RECONCILED") throw new InvalidOperationException($"{S(r, "code")} did not reconcile: {exec.Error}");
        }
    }

    private static async Task<List<Check>> CompareAll(IServiceProvider sp, string fixture)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContextAsync();
        IEnumerable<JsonElement> Ref(string t) => FixtureImporter.ReadNdjson(Path.Combine(fixture, "reference", $"{t}.ndjson.gz"));
        var runs = Ref("CalculationRun").ToDictionary(r => G(r, "id"), r => S(r, "code"));
        var runIds = runs.Keys.ToList();
        var checks = new List<Check>();

        Check Compare(string name, decimal tol, string tolText, IEnumerable<(string Key, decimal Ref, decimal? Ours)> pairs)
        {
            var list = pairs.ToList();
            var diffs = list.Select(p => (p.Key, p.Ref, p.Ours, Diff: p.Ours is null ? decimal.MaxValue : Math.Abs(p.Ours.Value - p.Ref))).ToList();
            var beyond = diffs.Where(d => d.Diff > tol).OrderByDescending(d => d.Diff).ToList();
            var max = diffs.Where(d => d.Diff != decimal.MaxValue).Select(d => d.Diff).DefaultIfEmpty(0m).Max();
            var worst = beyond.Take(8).Select(d => d.Ours is null
                ? $"{d.Key}: reference {d.Ref:N4}, missing in rebuild"
                : $"{d.Key}: reference {d.Ref:N4}, rebuild {d.Ours:N4} (Δ {d.Ours - d.Ref:N4})").ToList();
            var c = new Check(name, tolText, list.Count, beyond.Count, max, worst);
            checks.Add(c);
            Console.WriteLine($"  {name,-46} {list.Count,6:N0} rows  max Δ {max,12:N4}  beyond {tolText}: {beyond.Count}");
            return c;
        }



        // 1. Run totals and residual buckets.
        var recons = await db.Reconciliations.AsNoTracking().Where(r => runIds.Contains(r.RunId)).ToDictionaryAsync(r => r.RunId);
        var refRecon = Ref("Reconciliation").ToList();
        var residuals = await db.ResidualBuckets.AsNoTracking().Where(r => runIds.Contains(r.RunId)).ToListAsync();
        var totals = new List<(string, decimal, decimal?)>();
        foreach (var r in refRecon)
        {
            var rid = G(r, "runId");
            recons.TryGetValue(rid, out var ours);
            totals.Add(($"{runs[rid]} GL total", D(r, "glTotal"), ours?.GlTotal));
            totals.Add(($"{runs[rid]} patient cost", D(r, "patientCost"), ours?.PatientCost));
        }
        // ROUNDING is excluded: the reference labelled any in-materiality gap ROUNDING, the rebuild
        // keeps anything above SAR 1 visible as unexplained (FR-7.4). Compared separately below.
        foreach (var g in Ref("ResidualBucket").Where(b => S(b, "bucket") != "ROUNDING").GroupBy(b => (Run: G(b, "runId"), Bucket: S(b, "bucket"))))
        {
            var ours = residuals.Where(x => x.RunId == g.Key.Run && x.Bucket == g.Key.Bucket).Select(x => (decimal?)x.Amount).DefaultIfEmpty(null).Sum();
            totals.Add(($"{runs[g.Key.Run]} {g.Key.Bucket}", g.Sum(b => D(b, "amount")), ours));
        }
        Compare("Run totals, per residual bucket", 0.01m, "SAR 0.01", totals);

        var gaps = refRecon.Select(r =>
        {
            var rid = G(r, "runId");
            // The reference stored its in-materiality gap as Reconciliation.unexplained and again
            // as a ROUNDING bucket; it is counted once here.
            var refGap = D(r, "unexplained");
            recons.TryGetValue(rid, out var ours);
            var ourGap = ours is null ? (decimal?)null : ours.Unexplained + residuals.Where(x => x.RunId == rid && x.Bucket == "ROUNDING").Sum(x => x.Amount);
            return ($"{runs[rid]} unexplained + rounding", refGap, ourGap);
        });
        Compare("Unexplained gap", 0.01m, "SAR 0.01", gaps);

        // 2. Cost-centre closing balances after support allocation.
        var ccNames = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code);
        var ccOurs = await db.CostCenterBalances.AsNoTracking().Where(b => runIds.Contains(b.RunId)).ToDictionaryAsync(b => (b.RunId, b.CostCenterId), b => b.FinalAmount);
        Compare("Cost-centre closing balances", 0.01m, "SAR 0.01", Ref("CostCenterBalance").Select(b =>
            ($"{runs[G(b, "runId")]} {ccNames.GetValueOrDefault(G(b, "costCenterId"), "?")}", D(b, "finalAmount"),
             ccOurs.TryGetValue((G(b, "runId"), G(b, "costCenterId")), out var v) ? v : (decimal?)null)));

        // 3. Resource effective rates and practical capacity.
        var resNames = await db.Resources.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Code);
        var unitOurs = await db.ResourceUnitCosts.AsNoTracking().Where(u => runIds.Contains(u.RunId)).ToDictionaryAsync(u => (u.RunId, u.ResourceId));
        var unitRef = Ref("ResourceUnitCost").ToList();
        Compare("Resource effective rates", 0.000001m, "6 dp", unitRef.Select(u =>
            ($"{runs[G(u, "runId")]} {resNames.GetValueOrDefault(G(u, "resourceId"), "?")}", D(u, "unitCost"),
             unitOurs.TryGetValue((G(u, "runId"), G(u, "resourceId")), out var v) ? v.UnitCost : (decimal?)null)));
        Compare("Resource used quantity", 0.01m, "0.01", unitRef.Select(u =>
            ($"{runs[G(u, "runId")]} {resNames.GetValueOrDefault(G(u, "resourceId"), "?")}", D(u, "usedMinutes"),
             unitOurs.TryGetValue((G(u, "runId"), G(u, "resourceId")), out var v) ? v.UsedMinutes : (decimal?)null)));
        Compare("Resource cost of unused capacity", 0.01m, "SAR 0.01", unitRef.Select(u =>
            ($"{runs[G(u, "runId")]} {resNames.GetValueOrDefault(G(u, "resourceId"), "?")}", D(u, "costOfUnusedCapacity"),
             unitOurs.TryGetValue((G(u, "runId"), G(u, "resourceId")), out var v) ? v.CostOfUnusedCapacity : (decimal?)null)));

        // 4. Procedure standard unit cost.
        var procNames = await db.Procedures.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code);
        var procOurs = await db.ProcedureCosts.AsNoTracking().Where(p => runIds.Contains(p.RunId)).ToDictionaryAsync(p => (p.RunId, p.ProcedureId, p.CostType));
        var procRef = Ref("ProcedureCost").ToList();
        Compare("Procedure standard unit cost", 0.01m, "SAR 0.01", procRef.Where(p => S(p, "costType") == "STANDARD").Select(p =>
            ($"{runs[G(p, "runId")]} {procNames.GetValueOrDefault(G(p, "procedureId"), "?")}", D(p, "unitCost"),
             procOurs.TryGetValue((G(p, "runId"), G(p, "procedureId"), "STANDARD"), out var v) ? v.UnitCost : (decimal?)null)));
        Compare("Procedure actual total cost", 0.01m, "SAR 0.01", procRef.Where(p => S(p, "costType") == "ACTUAL").Select(p =>
            ($"{runs[G(p, "runId")]} {procNames.GetValueOrDefault(G(p, "procedureId"), "?")}", D(p, "totalCost"),
             procOurs.TryGetValue((G(p, "runId"), G(p, "procedureId"), "ACTUAL"), out var v) ? v.TotalCost : (decimal?)null)));

        // 5. Encounter totals.
        var encNos = await db.Encounters.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.EncounterNo);
        var encOurs = await db.EncounterCosts.AsNoTracking().Where(e => runIds.Contains(e.RunId)).ToDictionaryAsync(e => (e.RunId, e.EncounterId), e => e.TotalCost);
        Compare("Encounter total cost", 0.01m, "SAR 0.01", Ref("EncounterCost").Select(e =>
            ($"{runs[G(e, "runId")]} {encNos.GetValueOrDefault(G(e, "encounterId"), "?")}", D(e, "totalCost"),
             encOurs.TryGetValue((G(e, "runId"), G(e, "encounterId")), out var v) ? v : (decimal?)null)));

        return checks;
    }

    private static async Task Write(string reportPath, List<Check> referencePath, List<Check> production)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Parity with the reference implementation");
        sb.AppendLine();
        sb.AppendLine($"Generated by `scripts/tools.sh parity` on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. Method (BRD 23.4): the reference's seeded source");
        sb.AppendLine("data was exported (`tools/fixture/export-reference.mjs`), loaded into SQL Server through the importer, costed by the rebuilt");
        sb.AppendLine("engine, and compared with the reference engine's own results for the same eight hospital-periods.");
        sb.AppendLine();
        sb.AppendLine("Two passes. **Pass 1** runs the rebuilt engine on the reference's rounding path (every distribution rounded to the halala,");
        sb.AppendLine("as the reference does), so a difference there is a defect in one implementation, not precision. **Pass 2** is the rebuild");
        sb.AppendLine("as it runs in production: money in DECIMAL, distributions at the four-place storage scale. Differences in pass 2 that");
        sb.AppendLine("vanish in pass 1 are the rebuild being more precise, and their size is recorded here as BRD 23.4 asks.");
        void Table(string title, List<Check> checks)
        {
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Granularity | Rows | Tolerance | Max difference | Beyond tolerance |");
            sb.AppendLine("|---|---:|---|---:|---:|");
            foreach (var c in checks) sb.AppendLine($"| {c.Granularity} | {c.Rows:N0} | {c.Tolerance} | {c.MaxDiff.ToString("N4", CultureInfo.InvariantCulture)} | {c.Beyond:N0} |");
            foreach (var c in checks.Where(c => c.Worst.Count > 0))
            {
                sb.AppendLine();
                sb.AppendLine($"**{c.Granularity}** — largest differences:");
                sb.AppendLine();
                foreach (var w in c.Worst) sb.AppendLine($"- {w}");
            }
        }
        Table("Pass 1 — reference rounding path", referencePath);
        Table("Pass 2 — production rounding (four places)", production);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, sb.ToString());
        Console.WriteLine($"Parity report written to {reportPath}");
    }
}
