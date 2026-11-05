using Verita.Engine;

namespace Verita.Engine.Tests;

/// <summary>A run costed by its costing model, small enough to check on paper.</summary>
public class CostingModelPipelineTests
{
    private static readonly Guid Hospital = Guid.NewGuid(), Dept = Guid.NewGuid(), Payer = Guid.NewGuid(), Cc = Guid.NewGuid();
    private static readonly Guid Lab = Guid.NewGuid(), Xray = Guid.NewGuid(), NoPct = Guid.NewGuid();
    private static readonly Guid Enc1 = Guid.NewGuid(), Enc2 = Guid.NewGuid(), Enc3 = Guid.NewGuid();
    private static readonly Guid Doctor = Guid.NewGuid(), Role = Guid.NewGuid();

    private static CostingModelContext Context() => new()
    {
        RunId = Guid.NewGuid(), HospitalId = Hospital, HospitalName = "H", PeriodCode = "2026 Sep", CostingVersionCode = "CV", CalculatedAt = DateTime.UtcNow,
        Encounters =
        [
            new(Enc1, Dept, null, Doctor, Payer, null, null, "OP", "CASH", 0),
            new(Enc2, Dept, null, null, Payer, null, null, "IP", "CASH", 2),
            new(Enc3, Dept, null, null, Payer, null, null, "ER", "CASH", 0),
        ],
        Lines =
        [
            new(Enc1, Lab, Cc, Doctor, 2m, "INV-1"),
            new(Enc1, NoPct, Cc, null, 1m, "INV-2"),
            new(Enc2, Xray, Cc, null, 1m, "INV-3"),
        ],
        Revenue = [new(Enc1, "REV-1", 150m), new(Enc2, "REV-3", 1000m), new(Enc2, "REF-9", -100m)],
        Procedures = new()
        {
            [Lab] = new(Lab, "Lab", false, "LAB", 100m),
            [Xray] = new(Xray, "Xray", false, "IMAGING", 1000m),
            [NoPct] = new(NoPct, "Other", false, "OTHER", 50m),
        },
        DirectTypes = new() { ["LAB"] = 80m, ["IMAGING"] = 50m },
        DirectItems = new() { [Xray] = 60m },
        ZeroFixedTypes = [],
        ZeroFixedItems = [],
        PackageProcedures = [],
        PackageDirectTypes = [],
        PackageDirectItems = [],
        IndirectTypes = new() { ["ALL"] = 2m },
        IndirectItems = [],
        EncounterOverheads = [new(1m, 5m), new(0m, 7m)],
        DoctorResource = new() { [Doctor] = Role },
        ListedResources = new() { [Role] = "Role", [Guid.Parse("00000000-0000-0000-0000-000000000001")] = "Idle role" },
        Labels = [],
    };

    [Fact]
    public async Task Costs_every_encounter_from_the_costing_model_and_stores_matching_results()
    {
        var sink = new MemorySink();
        var outcome = await new CostingModelPipeline(sink).ExecuteAsync(Context(), default);
        var rows = sink.Rows("AGGREGATE");
        var cost = rows.EncounterCosts.ToDictionary(c => c.EncounterId);

        // Lab: 2 × 100 × 80% direct, 2 × 100 × 2% indirect. No % at all: SAR 0 direct, 2% indirect from All.
        Assert.Equal(160m, cost[Enc1].DirectCost);
        Assert.Equal(5m, cost[Enc1].IndirectCost);
        // Overheads on contracted value 250: 1% = 2.50 under the 5 cap, plus 7 fixed.
        Assert.Equal(9.5m, cost[Enc1].OverheadCost);
        Assert.Equal(174.5m, cost[Enc1].TotalCost);
        Assert.Equal(-24.5m, cost[Enc1].OperatingMargin);
        // The procedure's own % wins over its type's, and the cap holds: 1% of 1000 is capped at 5.
        Assert.Equal(600m, cost[Enc2].DirectCost);
        Assert.Equal(12m, cost[Enc2].OverheadCost);
        Assert.Equal(900m, cost[Enc2].Revenue);
        // No services: only the fixed overhead.
        Assert.Equal(7m, cost[Enc3].TotalCost);
        Assert.Equal(3, rows.EncounterCosts.Count);

        var whole = rows.DimensionCosts.Single(d => d.Dimension == "hospital");
        Assert.Equal(rows.EncounterCosts.Sum(c => c.TotalCost), whole.TotalCost);
        Assert.Equal(outcome.PatientCost, whole.TotalCost);
        Assert.Equal(3, whole.Encounters);

        // Every view of the run adds back to the same money.
        foreach (var dimension in new[] { "department", "payer", "encounterType", "resource", "costCentre" })
        {
            var view = rows.DimensionCosts.Where(d => d.Dimension == dimension).ToList();
            Assert.Equal(whole.TotalCost, view.Sum(d => d.TotalCost));
            Assert.Equal(whole.Revenue, view.Sum(d => d.Revenue));
        }
        var resources = rows.DimensionCosts.Where(d => d.Dimension == "resource").ToDictionary(d => d.Label);
        Assert.Equal(160m, resources["Role"].DirectCost);
        Assert.Equal(150m, resources["Role"].Revenue);
        Assert.Equal(0, resources["Idle role"].Encounters);
        // The refund has no invoice line, so it stays Not assigned with the doctorless X-ray's revenue.
        Assert.Equal(900m, resources["Not assigned"].Revenue);

        Assert.Contains(sink.Rows("VALIDATE").Issues, i => i.CheckCode == "DQ-MODEL-NO-MARGIN");
        Assert.Equal("RECONCILED", sink.Statuses[^1]);
    }

    [Fact]
    public async Task Package_costing_supersedes_direct_costing_only_on_packages_that_have_it()
    {
        Guid set = Guid.NewGuid(), byType = Guid.NewGuid(), unset = Guid.NewGuid();
        var ctx = Context();
        ctx.Lines.AddRange([new(Enc3, set, Cc, null, 1m, "INV-4"), new(Enc3, byType, Cc, null, 1m, "INV-5"), new(Enc3, unset, Cc, null, 1m, "INV-6")]);
        ctx.Procedures[set] = new(set, "Own %", false, "LAB", 1000m);
        ctx.Procedures[byType] = new(byType, "Type %", false, "SURGERY", 1000m);
        ctx.Procedures[unset] = new(unset, "No package %", false, "LAB", 1000m);
        ctx.PackageProcedures.UnionWith([set, byType, unset]);
        ctx.PackageDirectItems[set] = 30m;
        ctx.PackageDirectTypes["SURGERY"] = 40m;
        // Xray is not a package, so the package % on its type must not reach it.
        ctx.PackageDirectTypes["IMAGING"] = 10m;

        var sink = new MemorySink();
        await new CostingModelPipeline(sink).ExecuteAsync(ctx, default);
        var rows = sink.Rows("AGGREGATE");
        var std = rows.ProcedureCosts.Where(c => c.CostType == "STANDARD").ToDictionary(c => c.ProcedureId);

        Assert.Equal(300m, std[set].UnitCost);
        Assert.Equal(400m, std[byType].UnitCost);
        // No package %: unchanged from Direct costing, LAB 80%.
        Assert.Equal(800m, std[unset].UnitCost);
        // Not a package: Direct costing, even with a package % on its type.
        Assert.Equal(600m, std[Xray].UnitCost);
        Assert.Equal(1500m, rows.EncounterCosts.Single(c => c.EncounterId == Enc3).DirectCost);
    }

    [Fact]
    public async Task A_procedure_with_no_price_costs_its_fixed_value()
    {
        Guid own = Guid.NewGuid(), byType = Guid.NewGuid(), byAll = Guid.NewGuid(), freePackage = Guid.NewGuid();
        var ctx = Context();
        ctx.Lines.AddRange([new(Enc3, own, Cc, null, 1m, "INV-4"), new(Enc3, byType, Cc, null, 1m, "INV-5"), new(Enc3, byAll, Cc, null, 2m, "INV-6")]);
        ctx.Procedures[own] = new(own, "Own value", false, "LAB", 0m);
        ctx.Procedures[byType] = new(byType, "Type value", false, "LAB", 0m);
        ctx.Procedures[byAll] = new(byAll, "All value", false, "SURGERY", 0m);
        ctx.Procedures[freePackage] = new(freePackage, "Free package", false, "LAB", 0m);
        ctx.PackageProcedures.Add(freePackage);
        ctx.ZeroFixedItems[own] = 40m;
        ctx.ZeroFixedTypes["LAB"] = 30m;
        ctx.ZeroFixedTypes["ALL"] = 20m;

        var sink = new MemorySink();
        await new CostingModelPipeline(sink).ExecuteAsync(ctx, default);
        var rows = sink.Rows("AGGREGATE");
        var std = rows.ProcedureCosts.Where(c => c.CostType == "STANDARD").ToDictionary(c => c.ProcedureId);

        Assert.Equal(40m, std[own].UnitCost);
        Assert.Equal(30m, std[byType].UnitCost);
        Assert.Equal(20m, std[byAll].UnitCost);
        Assert.Equal(110m, rows.EncounterCosts.Single(c => c.EncounterId == Enc3).DirectCost);
        // A priced procedure keeps its %, and a package is not on the zero-price list.
        Assert.Equal(80m, std[Lab].UnitCost);
        Assert.Equal(0m, std[freePackage].UnitCost);
    }

    [Fact]
    public async Task A_package_carries_no_indirect_cost_but_its_other_lines_do()
    {
        var package = Guid.NewGuid();
        var ctx = Context();
        ctx.Lines.Add(new(Enc3, package, Cc, null, 1m, "INV-4"));
        ctx.Procedures[package] = new(package, "Package", false, "LAB", 1000m);
        ctx.PackageProcedures.Add(package);

        var sink = new MemorySink();
        await new CostingModelPipeline(sink).ExecuteAsync(ctx, default);
        var rows = sink.Rows("AGGREGATE");
        var std = rows.ProcedureCosts.Where(c => c.CostType == "STANDARD").ToDictionary(c => c.ProcedureId);

        Assert.Equal(0m, std[package].IndirectCost);
        Assert.Equal(800m, std[package].DirectCost);
        // Enc3 holds only the package: no indirect, though All is 2%. Lab on Enc1 keeps its 2%.
        Assert.Equal(0m, rows.EncounterCosts.Single(c => c.EncounterId == Enc3).IndirectCost);
        Assert.Equal(5m, rows.EncounterCosts.Single(c => c.EncounterId == Enc1).IndirectCost);
    }
}
