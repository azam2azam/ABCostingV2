using Verita.Domain;
using Verita.Engine;
using Verita.Engine.Stages;

namespace Verita.Engine.Tests;

/// <summary>Captures what the pipeline would persist, without a database (NFR-21).</summary>
internal sealed class MemorySink : IStageSink
{
    public readonly Dictionary<string, (StepResult Step, StageOutput Rows)> Stages = [];
    public readonly List<string> Statuses = [];
    public Task StageStartedAsync(Catalogue.RunStepDef step, CancellationToken ct) => Task.CompletedTask;
    public Task StageCompletedAsync(Catalogue.RunStepDef step, StepResult result, StageOutput rows, TimeSpan d, CancellationToken ct) { Stages[step.Code] = (result, rows); return Task.CompletedTask; }
    public Task StageFailedAsync(Catalogue.RunStepDef step, string message, TimeSpan d, CancellationToken ct) => Task.CompletedTask;
    public Task StatusAsync(string status, CancellationToken ct) { Statuses.Add(status); return Task.CompletedTask; }
    public StageOutput Rows(string code) => Stages[code].Rows;
}

/// <summary>
/// A small hospital built by hand so every number can be re-derived on paper. Two reciprocal
/// support centres, one overhead centre, three operating centres (one with no measured activity),
/// a capacity pool, a supply pool, and two encounters.
/// </summary>
internal static class Fixture
{
    public static readonly Guid Hosp = G("hosp"), Period = G("period"), Version = G("v1");
    public static readonly Guid CcIt = G("cc-it"), CcFac = G("cc-fac"), CcAdmin = G("cc-admin"), CcRad = G("cc-rad"), CcLab = G("cc-lab"), CcPhys = G("cc-phys");
    public static readonly Guid PIt = G("p-it"), PFac = G("p-fac"), PAdmin = G("p-admin"), PRadStaff = G("p-radstaff"), PSupply = G("p-supply"), PRadOh = G("p-radoh"), PLabOh = G("p-laboh"), PPhysOh = G("p-physoh");
    public static readonly Guid Tech = G("r-tech"), Scanner = G("r-scanner"), Contrast = G("r-contrast");
    public static readonly Guid DHead = G("d-head"), DArea = G("d-area"), DTime = G("d-time"), DVol = G("d-vol");
    public static readonly Guid Scan = G("a-scan"), Test = G("a-test"), Therapy = G("a-therapy");
    public static readonly Guid Mri = G("pr-mri"), LabTest = G("pr-lab");
    public static readonly Guid E1 = G("e1"), E2 = G("e2"), S1 = G("s1"), S2 = G("s2"), S3 = G("s3");
    public static readonly Guid AccSal = G("acc-sal"), AccDrug = G("acc-drug"), AccOther = G("acc-other");
    public const int Days = 30;

    public static Guid G(string k) => IdGen.FromKey("test:" + k);

    // Capacity: practical minutes a year and annual cost, so rate = cost / practical.
    public const decimal TechPractical = 36_500m, TechAnnual = 73_000m;       // 2.00 / min
    public const decimal ScannerPractical = 36_500m, ScannerAnnual = 182_500m; // 5.00 / min

    public static decimal Available(decimal practical) => practical * Days / 365m;

    public static RunContext Build(ScenarioOverrides? overrides = null, decimal? futureRate = null, int scale = 4)
    {
        var o = overrides ?? new ScenarioOverrides();
        var ccs = new List<CcInfo>
        {
            new(CcIt, "IT", "Information technology", "SUPPORT", 10, G("dept")),
            new(CcFac, "FAC", "Facilities", "SUPPORT", 20, G("dept")),
            new(CcAdmin, "ADMIN", "Administration", "OVERHEAD", 30, G("dept")),
            new(CcRad, "RAD", "Radiology", "PRODUCTION", 100, G("dept")),
            new(CcLab, "LAB", "Laboratory", "PRODUCTION", 110, G("dept")),
            new(CcPhys, "PHYS", "Physiotherapy", "PRODUCTION", 120, G("dept")),
        };
        var funded = new HashSet<Guid> { PRadStaff, PSupply };
        PoolInfo Pool(Guid id, Guid cc, string name) => new(id, name, name, cc, "INDIRECT", "STEP_DOWN", null, !funded.Contains(id));
        var pools = new List<PoolInfo>
        {
            Pool(PIt, CcIt, "IT"), Pool(PFac, CcFac, "Facilities"), Pool(PAdmin, CcAdmin, "Admin"), Pool(PRadStaff, CcRad, "Radiology staff & equipment"),
            Pool(PSupply, CcRad, "Contrast"), Pool(PRadOh, CcRad, "Radiology overhead"), Pool(PLabOh, CcLab, "Lab overhead"), Pool(PPhysOh, CcPhys, "Physio overhead"),
        };
        decimal M(Guid id, decimal v) => v * o.Get(o.ResourceRate, id);
        decimal C(Guid id, decimal v) => v * o.Get(o.ResourceCapacity, id);
        var resources = new Dictionary<Guid, ResInfo>
        {
            [Tech] = new(Tech, "TECH", "Technologist", "LABOR_TECH", "MIN", true, CcRad, PRadStaff, M(Tech, futureRate ?? 2m), G("rate-tech"), new(C(Tech, TechPractical / .8m), C(Tech, TechPractical), .8m, TechAnnual)),
            [Scanner] = new(Scanner, "SCAN", "Scanner", "EQUIPMENT", "MIN", true, CcRad, PRadStaff, M(Scanner, 5m), G("rate-scan"), new(C(Scanner, ScannerPractical / .8m), C(Scanner, ScannerPractical), .8m, ScannerAnnual)),
            [Contrast] = new(Contrast, "CON", "Contrast", "DRUG", "DOSE", false, CcRad, PSupply, M(Contrast, 100m), G("rate-con"), null),
        };
        var drivers = new Dictionary<Guid, DriverInfo>
        {
            [DHead] = new(DHead, "HEAD", "HEAD", "Headcount", "HEADCOUNT", "HEADCOUNT", "staff in post"),
            [DArea] = new(DArea, "AREA", "AREA", "Floor area", "SQM", "AREA", "square metres"),
            [DTime] = new(DTime, "TIME", "TIME", "Minutes", "MIN", "TIME", "measured minutes"),
            [DVol] = new(DVol, "VOL", "VOL", "Tests", "TEST", "VOLUME", "occurrences"),
        };
        var volumes = new Dictionary<(Guid, Guid), decimal>
        {
            [(DHead, CcIt)] = 5, [(DHead, CcFac)] = 10, [(DHead, CcAdmin)] = 5, [(DHead, CcRad)] = 20, [(DHead, CcLab)] = 10, [(DHead, CcPhys)] = 10,
            [(DArea, CcIt)] = 100, [(DArea, CcFac)] = 50, [(DArea, CcAdmin)] = 150, [(DArea, CcRad)] = 400, [(DArea, CcLab)] = 200, [(DArea, CcPhys)] = 100,
        };
        RuleInfo Rule(string code, Guid pool, Guid cc, Guid? driver, int stage, string method = "VOLUME") =>
            new(G(code), code, code, pool, cc, driver, method, stage == 1 ? "RECIPROCAL" : stage == 2 ? "STEP_DOWN" : "ABC", stage, 100, 1m, null, []);
        var rules = new List<RuleInfo>
        {
            Rule("R-IT", PIt, CcIt, DHead, 1), Rule("R-FAC", PFac, CcFac, DArea, 1), Rule("R-ADMIN", PAdmin, CcAdmin, DHead, 2),
            Rule("R-RAD", PRadOh, CcRad, DTime, 3, "TIME"), Rule("R-LAB", PLabOh, CcLab, DVol, 3), Rule("R-PHYS", PPhysOh, CcPhys, DTime, 3, "TIME"),
        };
        var acts = new Dictionary<Guid, ActInfo>
        {
            [Scan] = new(Scan, "SCAN", "SCAN", "MRI acquisition", "Imaging", CcRad, G("dept"), "DIRECT", 30, 10, 90,
                [new(Tech, 30, "MIN"), new(Scanner, 30, "MIN"), new(Contrast, 1, "DOSE")], DTime),
            [Test] = new(Test, "TEST", "TEST", "Lab test", "Lab", CcLab, G("dept"), "DIRECT", 10, 5, 30, [new(Tech, 10, "MIN")], DVol),
            [Therapy] = new(Therapy, "THER", "THER", "Therapy session", "Physio", CcPhys, G("dept"), "DIRECT", 45, 30, 60, [new(Tech, 45, "MIN")], DTime),
        };
        var procs = new Dictionary<Guid, ProcInfo>
        {
            [Mri] = new(Mri, "MRI", "MRI", "MRI brain", "IMAGING", "Imaging", 3, G("dept"), null, 2000, [new(Scan, 1)], []),
            [LabTest] = new(LabTest, "LAB", "LAB", "Blood panel", "LAB", "Lab", 1, G("dept"), null, 150, [new(Test, 1)], []),
        };

        // The ledger, sized so every currency unit has a home.
        var capacityCost = Available(TechPractical) * 2m + Available(ScannerPractical) * 5m;
        var postings = new List<PostingRow>
        {
            new(AccOther, "5900", "Other", CcIt, 1000), new(AccOther, "5900", "Other", CcFac, 2000), new(AccOther, "5900", "Other", CcAdmin, 3000),
            new(AccSal, "5100", "Salaries", CcRad, Money.R4(capacityCost)), new(AccDrug, "5300", "Drugs", CcRad, 250),
            new(AccOther, "5900", "Other", CcRad, 500), new(AccOther, "5900", "Other", CcLab, 300), new(AccOther, "5900", "Other", CcPhys, 400),
        };
        var mappings = new List<MappingRow>
        {
            new(AccOther, CcIt, PIt, 1, "5900", "IT"), new(AccOther, CcFac, PFac, 1, "5900", "FAC"), new(AccOther, CcAdmin, PAdmin, 1, "5900", "ADMIN"),
            new(AccSal, CcRad, PRadStaff, 1, "5100", "RAD"), new(AccDrug, CcRad, PSupply, 1, "5300", "RAD"), new(AccOther, CcRad, PRadOh, 1, "5900", "RAD"),
            new(AccOther, CcLab, PLabOh, 1, "5900", "LAB"), new(AccOther, CcPhys, PPhysOh, 1, "5900", "PHYS"),
        };

        return new RunContext
        {
            RunId = G("run"), RunCode = "RUN-TEST", HospitalId = Hosp, HospitalCode = "TST", HospitalName = "Test Hospital",
            PeriodId = Period, PeriodCode = "2026-08", PeriodDays = Days, PeriodStart = new DateOnly(2026, 8, 1), PeriodEnd = new DateOnly(2026, 8, 30),
            CostingVersionId = Version, CostingVersionCode = "CV-TEST", CalculatedAt = new DateTime(2026, 9, 1),
            CostCenters = ccs, Pools = pools, Resources = resources, Drivers = drivers, DriverVolumes = volumes, Rules = rules,
            Activities = acts, Procedures = procs, Packages = new Dictionary<Guid, PackageInfo>(),
            Postings = postings, Mappings = mappings,
            Encounters = [new(E1, 1, G("payer"), null, null, false), new(E2, 0, G("payer"), null, null, false)],
            Services = [new(S1, E1, Mri, CcRad, null, 1), new(S2, E1, LabTest, CcLab, null, 1), new(S3, E2, Mri, CcRad, null, 1)],
            Revenue = [new(E1, Mri, G("payer"), 2000), new(E1, LabTest, G("payer"), 150), new(E2, Mri, G("payer"), 2000)],
            RateSpans = [new(Tech, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), new(Tech, new DateOnly(2027, 1, 1), null)],
            Overrides = o, DistributionScale = scale,
        };
    }

    public static List<TxnRow> Transactions() =>
    [
        new(E1, S1, Scan, Tech, CcRad, 35, "MIN", "HIS-1-SCAN-TECH"), new(E1, S1, Scan, Scanner, CcRad, 35, "MIN", "HIS-1-SCAN-SCANNER"),
        new(E1, S1, Scan, Contrast, CcRad, 1, "DOSE", "PHARM-1-SCAN-CON"), new(E1, S2, Test, Tech, CcLab, 10, "MIN", "LIS-1-TEST-TECH"),
        new(E2, S3, Scan, Tech, CcRad, 30, "MIN", "HIS-2-SCAN-TECH"), new(E2, S3, Scan, Scanner, CcRad, 30, "MIN", "HIS-2-SCAN-SCANNER"),
        new(E2, S3, Scan, Contrast, CcRad, 1, "DOSE", "PHARM-2-SCAN-CON"),
    ];

    public static async Task<(PipelineOutcome Outcome, MemorySink Sink)> Run(RunContext? ctx = null, List<TxnRow>? txns = null)
    {
        var sink = new MemorySink();
        var outcome = await new Pipeline(sink).ExecuteAsync(ctx ?? Build(), txns ?? Transactions(), new PipelineOptions());
        return (outcome, sink);
    }
}

public class MoneyTests
{
    /// <summary>Stage 0 exit condition: distribute() over random amounts and weights (CR-2).</summary>
    [Fact]
    public void Distribute_always_sums_back_to_the_amount_exactly()
    {
        var rng = new Random(20260921);
        for (var i = 0; i < 5000; i++)
        {
            var amount = Math.Round((decimal)rng.NextDouble() * 1_000_000m - 100m, 4);
            var weights = Enumerable.Range(0, rng.Next(1, 40)).Select(_ => Math.Round((decimal)rng.NextDouble() * 1000m, 6)).ToArray();
            if (weights.Sum() == 0) continue;
            var parts = Money.Distribute(amount, weights);
            Assert.Equal(amount, parts.Sum());
            Assert.All(parts, p => Assert.Equal(p, Math.Round(p, 4)));
        }
    }

    [Fact]
    public void Distribute_at_two_places_matches_the_reference_rounding_path()
    {
        var parts = Money.Distribute(100m, [1m, 1m, 1m], 2);
        Assert.Equal(100m, parts.Sum());
        Assert.Equal(2, parts.Count(p => p == 33.33m));
    }

    [Fact]
    public void Zero_denominator_returns_null_not_a_substitute() => Assert.Null(Money.SafeDiv(10m, 0m));

    [Fact]
    public void Percentile_and_stddev_are_decimal()
    {
        decimal[] v = [1, 2, 3, 4, 100];
        Assert.Equal(3m, Money.Percentile(v, 50));
        Assert.Equal(4m, Money.Percentile(v, 75));
        Assert.True(Math.Abs(Money.StdDev([2, 4, 4, 4, 5, 5, 7, 9]) - 2.1380899352993950m) < 0.0000001m);
    }
}

public class PipelineTests
{
    [Fact]
    public async Task Run_reconciles_with_every_currency_unit_in_a_named_place()
    {
        var (o, sink) = await Fixture.Run();
        Assert.Equal("RECONCILED", o.Status);
        // AC-1: GL = patient cost + named residuals + unexplained, unexplained under SAR 1.
        var rec = sink.Rows("RECONCILE").Reconciliations.Single();
        var residuals = sink.Rows("RECONCILE").Residuals.Sum(r => r.Amount);
        Assert.Equal(rec.GlTotal, rec.PatientCost + residuals + rec.Unexplained);
        Assert.True(Math.Abs(rec.Unexplained) < 1m, $"unexplained {rec.Unexplained}");
        Assert.Equal("PASS", rec.Status);
    }

    [Fact]
    public async Task Allocation_stages_neither_create_nor_destroy_money()
    {
        var (_, sink) = await Fixture.Run();
        // AC-3, asserted per stage from the RunStep money in and out.
        var support = sink.Stages["ALLOCATE_SUPPORT"].Step;
        Assert.Equal(support.MoneyIn, support.MoneyOut);
        var activity = sink.Stages["ALLOCATE_ACTIVITY"].Step;
        Assert.True(Math.Abs(activity.MoneyIn - activity.MoneyOut) <= 0.0001m, $"activity in {activity.MoneyIn} out {activity.MoneyOut}");
        // Every support and overhead centre closes at zero (FR-2.2, stage 5).
        foreach (var b in sink.Rows("ALLOCATE_SUPPORT").CcBalances.Where(b => b.CostCenterId == Fixture.CcIt || b.CostCenterId == Fixture.CcFac || b.CostCenterId == Fixture.CcAdmin))
            Assert.True(Math.Abs(b.FinalAmount) <= 0.01m, $"centre closed at {b.FinalAmount}");
    }

    [Fact]
    public async Task Reciprocal_centres_never_close_negative()
    {
        var (_, sink) = await Fixture.Run();
        // CR-5: inflow recorded on the balance sheet and the next iteration both.
        Assert.All(sink.Rows("ALLOCATE_SUPPORT").CcBalances, b => Assert.True(b.FinalAmount >= -0.01m));
        Assert.All(sink.Rows("ALLOCATE_SUPPORT").Allocations, a => Assert.NotEqual(a.SourceCostCenterId, a.TargetCostCenterId));
    }

    [Fact]
    public async Task Idle_capacity_never_reaches_an_encounter()
    {
        var (_, sink) = await Fixture.Run();
        // AC-7: encounter direct + indirect equals measured quantity x rate, nothing more.
        var e2 = sink.Rows("COST_ACTUAL").EncounterCosts.Single(e => e.EncounterId == Fixture.E2);
        Assert.Equal(30m * 2m + 100m, e2.DirectCost);
        Assert.Equal(30m * 5m, e2.IndirectCost);
        var idle = sink.Rows("RECONCILE").Residuals.Single(r => r.Bucket == "UNUSED_CAPACITY").Amount;
        var expectedIdle = (Fixture.Available(Fixture.TechPractical) - 75m) * 2m + (Fixture.Available(Fixture.ScannerPractical) - 65m) * 5m;
        Assert.True(Math.Abs(idle - expectedIdle) <= 0.0002m);
    }

    [Fact]
    public async Task Zero_driver_volume_routes_to_a_residual_never_a_substitute_denominator()
    {
        var (_, sink) = await Fixture.Run();
        // CR-13: physiotherapy has a balance and no measured activity.
        var bucket = sink.Rows("RECONCILE").Residuals.Single(r => r.Bucket == "NO_DRIVER_VOLUME");
        Assert.True(bucket.Amount > 400m);
        Assert.Contains(sink.Rows("ALLOCATE_ACTIVITY").Allocations, a => a.TargetType == "RESIDUAL" && a.SourceCostCenterId == Fixture.CcPhys);
    }

    [Fact]
    public async Task Supply_not_issued_is_named_wastage()
    {
        var (_, sink) = await Fixture.Run();
        var waste = sink.Rows("RECONCILE").Residuals.Single(r => r.Bucket == "NON_PATIENT_ACTIVITY" && r.Label.StartsWith("Supply"));
        Assert.Equal(50m, waste.Amount);
    }

    [Fact]
    public async Task Variance_effects_sum_exactly_to_actual_minus_standard()
    {
        var (_, sink) = await Fixture.Run();
        var comps = sink.Rows("VARIANCE").VarianceComponents;
        Assert.NotEmpty(comps);
        // AC-2, per line.
        Assert.All(comps, c => Assert.Equal(c.TotalEffect, c.RateEffect + c.QuantityEffect + c.MixEffect));
        // CR-6: overhead appears on both sides, so it is not reported wholesale as favourable.
        var overhead = comps.SingleOrDefault(c => c.ComponentType == "OVERHEAD" && c.VarianceId == sink.Rows("VARIANCE").Variances.Single(v => v.ScopeId == Fixture.Mri).Id);
        Assert.NotNull(overhead);
        Assert.True(overhead!.ActualQty > 0);
    }

    [Fact]
    public async Task Contribution_margin_is_never_below_operating_margin()
    {
        var (_, sink) = await Fixture.Run();
        Assert.All(sink.Rows("COST_ACTUAL").EncounterCosts, e => Assert.True(e.ContributionMargin >= e.OperatingMargin));
    }

    [Fact]
    public async Task Every_component_carries_its_arithmetic()
    {
        var (_, sink) = await Fixture.Run();
        var comps = sink.Stages.Values.SelectMany(s => s.Rows.Components).ToList();
        Assert.NotEmpty(comps);
        // The database check constraint, asserted here too.
        Assert.All(comps, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.CalcExpression));
            Assert.Equal(Math.Round(c.Quantity * c.UnitCost, 4, MidpointRounding.AwayFromZero), c.Amount);
        });
    }

    /// <summary>BRD 23.5: re-derive the procedure's standard cost from the recipe and rates, without the engine.</summary>
    [Fact]
    public async Task Standard_cost_matches_an_independent_derivation()
    {
        var (_, sink) = await Fixture.Run();
        var published = sink.Rows("COST_STANDARD").ProcedureCosts.Single(p => p.ProcedureId == Fixture.Mri).UnitCost;
        var direct = 30m * 2m + 30m * 5m + 1m * 100m;
        var actOverhead = sink.Rows("ALLOCATE_ACTIVITY").Allocations.Where(a => a.TargetActivityId == Fixture.Scan).Sum(a => a.Amount);
        var perOccurrence = actOverhead / 2m; // two MRI occurrences
        Assert.Equal(Math.Round(direct + Math.Round(perOccurrence, 6, MidpointRounding.AwayFromZero), 4, MidpointRounding.AwayFromZero), published);
    }

    [Fact]
    public async Task Same_inputs_give_identical_results()
    {
        // AC-6.
        var (_, a) = await Fixture.Run();
        var (_, b) = await Fixture.Run();
        Assert.Equal(a.Rows("COST_ACTUAL").EncounterCosts.Select(e => e.TotalCost), b.Rows("COST_ACTUAL").EncounterCosts.Select(e => e.TotalCost));
        Assert.Equal(a.Rows("ALLOCATE_SUPPORT").Allocations.Select(x => x.Amount), b.Rows("ALLOCATE_SUPPORT").Allocations.Select(x => x.Amount));
    }

    [Fact]
    public async Task Blocking_issue_stops_the_run_before_money_moves()
    {
        var txns = Fixture.Transactions();
        txns.Add(new(Fixture.E2, Fixture.S3, Fixture.Scan, Fixture.Tech, Fixture.CcRad, -5, "MIN", "HIS-2-NEG"));
        var sink = new MemorySink();
        var ex = await Assert.ThrowsAsync<RunFailedException>(() => new Pipeline(sink).ExecuteAsync(Fixture.Build(), txns, new PipelineOptions(Force: true)));
        Assert.Equal("VALIDATE", ex.StepCode);
        Assert.False(sink.Stages.ContainsKey("POOL_BUILD"));
        Assert.Contains(sink.Rows("VALIDATE").Issues, i => i.CheckCode == "DQ-COST-NEGATIVE");
    }

    [Fact]
    public async Task Durations_are_checked_per_occurrence_not_per_row()
    {
        // CR-11: 3 x 90 = 270 is the fence; 200 minutes in one row is fine, 400 is not.
        var txns = Fixture.Transactions();
        txns.Add(new(Fixture.E2, Fixture.S3, Fixture.Scan, Fixture.Tech, Fixture.CcRad, 400, "MIN", "HIS-2-LONG"));
        var (_, sink) = await Fixture.Run(txns: txns);
        Assert.Contains(sink.Rows("VALIDATE").Issues, i => i.CheckCode == "DQ-DURATION-IMPOSSIBLE");
    }

    [Fact]
    public async Task Duplicate_source_references_are_reported()
    {
        var txns = Fixture.Transactions();
        txns.Add(txns[0]);
        var sink = new MemorySink();
        await Assert.ThrowsAsync<RunFailedException>(() => new Pipeline(sink).ExecuteAsync(Fixture.Build(), txns, new PipelineOptions()));
        Assert.Contains(sink.Rows("VALIDATE").Issues, i => i.CheckCode == "DQ-TXN-DUPLICATE" && i.Severity == "ERROR");
    }

    [Fact]
    public async Task Overlapping_rates_are_blocking()
    {
        var ctx = Fixture.Build();
        var bad = new RunContext
        {
            RunId = ctx.RunId, RunCode = ctx.RunCode, HospitalId = ctx.HospitalId, HospitalCode = ctx.HospitalCode, HospitalName = ctx.HospitalName,
            PeriodId = ctx.PeriodId, PeriodCode = ctx.PeriodCode, PeriodDays = ctx.PeriodDays, PeriodStart = ctx.PeriodStart, PeriodEnd = ctx.PeriodEnd,
            CostingVersionId = ctx.CostingVersionId, CostingVersionCode = ctx.CostingVersionCode, CostCenters = ctx.CostCenters, Pools = ctx.Pools,
            Resources = ctx.Resources, Drivers = ctx.Drivers, DriverVolumes = ctx.DriverVolumes, Rules = ctx.Rules, Activities = ctx.Activities,
            Procedures = ctx.Procedures, Packages = ctx.Packages, Postings = ctx.Postings, Mappings = ctx.Mappings, Encounters = ctx.Encounters,
            Services = ctx.Services, Revenue = ctx.Revenue,
            // CR-9: two open-ended ranges.
            RateSpans = [new(Fixture.Tech, new DateOnly(2026, 1, 1), null), new(Fixture.Tech, new DateOnly(2026, 6, 1), null)],
        };
        var sink = new MemorySink();
        await Assert.ThrowsAsync<RunFailedException>(() => new Pipeline(sink).ExecuteAsync(bad, Fixture.Transactions(), new PipelineOptions(Force: true)));
        Assert.Contains(sink.Rows("VALIDATE").Issues, i => i.CheckCode == "DQ-RATE-OVERLAP" && i.Severity == "BLOCKING");
    }
}

public class SimulationTests
{
    private static ScenarioOverrides For(params LeverInput[] levers) =>
        Simulation.BuildOverrides(levers, [new(Fixture.Tech, "LABOR_TECH"), new(Fixture.Scanner, "EQUIPMENT"), new(Fixture.Contrast, "DRUG")],
            [Fixture.Scan, Fixture.Test, Fixture.Therapy], [Fixture.Mri, Fixture.LabTest], [Fixture.G("payer")]);

    private static decimal Patient(MemorySink s) => s.Rows("COST_ACTUAL").EncounterCosts.Sum(e => e.TotalCost);
    private static decimal Idle(MemorySink s) => s.Rows("RECONCILE").Residuals.Where(r => r.Bucket == "UNUSED_CAPACITY").Sum(r => r.Amount);

    [Fact]
    public async Task Drug_inflation_raises_patient_cost()
    {
        var (_, b) = await Fixture.Run();
        var (_, s) = await Fixture.Run(Fixture.Build(For(new LeverInput("DRUG_PRICE", "ALL", null, "All drugs", "PCT", 12))));
        Assert.Equal(Patient(b) + 2 * 12m, Patient(s));
    }

    /// <summary>CR-7 / FR-8.6: a utilisation lever lands entirely in idle capacity.</summary>
    [Fact]
    public async Task Staff_utilisation_leaves_patient_cost_unchanged_and_releases_idle_capacity()
    {
        var (_, b) = await Fixture.Run();
        var (_, s) = await Fixture.Run(Fixture.Build(For(new LeverInput("STAFF_UTILISATION", "ALL", null, "Labour", "PCT", 10))));
        Assert.Equal(Patient(b), Patient(s));
        Assert.True(Idle(s) < Idle(b));
    }

    [Fact]
    public async Task Reimbursement_cut_changes_revenue_not_cost()
    {
        var (_, b) = await Fixture.Run();
        var (_, s) = await Fixture.Run(Fixture.Build(For(new LeverInput("REIMBURSEMENT", "ALL", null, "All payers", "PCT", -5))));
        Assert.Equal(Patient(b), Patient(s));
        Assert.True(s.Rows("COST_ACTUAL").EncounterCosts.Sum(e => e.Revenue) < b.Rows("COST_ACTUAL").EncounterCosts.Sum(e => e.Revenue));
    }
}
