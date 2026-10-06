namespace Verita.Domain;

/// <summary>
/// The data-quality check register (FR-9.3). Each check is declared once with its code, name,
/// category, severity and the question it asks; the Data Quality screen, the run gate and the
/// documentation all read this one list.
/// </summary>
public static class QualityChecks
{
    public sealed record Check(string Code, string Name, string Category, string Severity, string Question, bool AfterCapacity = false);

    public static readonly Check[] All =
    [
        new("DQ-RATE-MISSING", "Resource without an effective rate", "Master data", "BLOCKING", "Does every resource consumed in the period have a rate effective at the run date?"),
        new("DQ-RATE-OVERLAP", "Overlapping resource rates", "Master data", "BLOCKING", "Do any two rates for the same resource cover the same date?"),
        new("DQ-COST-NEGATIVE", "Negative cost recorded", "Calculation", "BLOCKING", "Has any transaction produced a negative cost?"),
        new("DQ-GL-UNMAPPED", "GL posting with no cost-pool mapping", "Financial", "ERROR", "Does every posting reach a cost pool?"),
        new("DQ-GL-WEIGHT", "GL mapping weights do not total 100%", "Financial", "ERROR", "Do the pool weights for an (account, cost centre) pair sum to 1?"),
        new("DQ-CC-NO-POOL", "Cost centre with spend but no pool", "Financial", "ERROR", "Does every cost centre that carries spend have somewhere to put it?"),
        new("DQ-ALLOC-WEIGHT", "Allocation percentages do not total 100%", "Allocation", "ERROR", "Do a percentage rule's targets sum to 1?"),
        new("DQ-ALLOC-POOLSHARE", "Rule pool shares do not total 100%", "Allocation", "ERROR", "Do the rules on one pool together move all of it, and no more?"),
        new("DQ-DRIVER-MISSING", "Cost pool without a driver", "Allocation", "ERROR", "Does every allocating pool name the driver that causes it?"),
        new("DQ-TXN-DUPLICATE", "Duplicate source transaction", "Source data", "ERROR", "Has the same source reference been loaded twice?"),
        new("DQ-PROC-UNMAPPED", "Procedure without a costing definition", "Master data", "ERROR", "Does every procedure delivered have a recipe under the run's costing version?"),
        new("DQ-DRIVER-ZERO", "Cost driver with no measured volume", "Allocation", "WARNING", "Did every driver used by a rule record volume this period?"),
        new("DQ-DURATION-IMPOSSIBLE", "Implausible activity duration", "Source data", "WARNING", "Is any recorded duration per occurrence beyond three times the activity's declared maximum?"),
        new("DQ-UTIL-ZERO", "Resource with zero utilisation", "Capacity", "WARNING", "Is the hospital paying for capacity nothing consumed?", AfterCapacity: true),
        new("DQ-UTIL-OVER", "Resource used beyond practical capacity", "Capacity", "WARNING", "Is any resource being worked beyond the capacity it is modelled at?", AfterCapacity: true),
        new("DQ-CONSUMPTION-ABNORMAL", "Abnormal resource consumption", "Clinical", "WARNING", "Did any encounter consume a resource far outside the normal range?", AfterCapacity: true),
        new("DQ-ACT-NO-RECIPE", "Activity with no standard recipe", "Master data", "WARNING", "Does every direct activity declare the resources it consumes?"),
        new("DQ-ENC-NO-SERVICE", "Encounter with no services", "Source data", "WARNING", "Did any encounter arrive with no clinical activity at all?"),
        new("DQ-ENC-NO-REVENUE", "Encounter with cost but no revenue", "Revenue", "WARNING", "Has any episode been costed with nothing billed against it?"),
    ];

    public static Check Get(string code) => All.First(c => c.Code == code);
}
