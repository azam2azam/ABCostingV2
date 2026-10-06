namespace Verita.Domain;

/// <summary>
/// The enum catalogue (NFR-22). Every enumerated column's legal values are declared once here.
/// The database check constraints, the engine, the API validators and the Angular labels
/// (served from /api/v1/meta) all read this file, so a column cannot drift from what the
/// interface displays.
/// </summary>
public static class Catalogue
{
    public static readonly string[] DeptTypes = ["CLINICAL", "ANCILLARY", "SUPPORT", "ADMIN"];
    public static readonly string[] CcTypes = ["PRODUCTION", "ANCILLARY", "SUPPORT", "OVERHEAD"];
    /// <summary>Cost centres that receive allocation but never originate it.</summary>
    public static readonly string[] OperatingCcTypes = ["PRODUCTION", "ANCILLARY"];

    public static readonly string[] NaturalClasses =
        ["SALARY", "BENEFITS", "DRUG", "CONSUMABLE", "IMPLANT", "DEPRECIATION", "MAINTENANCE", "UTILITY", "IT", "RENT", "OTHER"];
    public static readonly string[] CostClasses = ["DIRECT", "INDIRECT"];

    public static readonly string[] ResourceTypes =
    [
        "LABOR_PHYSICIAN", "LABOR_NURSE", "LABOR_TECH", "LABOR_ALLIED", "DRUG", "CONSUMABLE",
        "IMPLANT", "BLOOD", "EQUIPMENT", "ROOM", "UTILITY", "IT_SERVICE", "FACILITY",
    ];

    public static readonly IReadOnlyDictionary<string, string> ResourceTypeLabel = new Dictionary<string, string>
    {
        ["LABOR_PHYSICIAN"] = "Physician", ["LABOR_NURSE"] = "Nursing", ["LABOR_TECH"] = "Technician",
        ["LABOR_ALLIED"] = "Allied health", ["DRUG"] = "Drug", ["CONSUMABLE"] = "Consumable",
        ["IMPLANT"] = "Implant", ["BLOOD"] = "Blood product", ["EQUIPMENT"] = "Equipment",
        ["ROOM"] = "Room / bed", ["UTILITY"] = "Utility", ["IT_SERVICE"] = "IT service",
        ["FACILITY"] = "Facility",
    };

    /// <summary>Resource types costed by time-driven ABC rather than by purchase price.</summary>
    public static readonly string[] CapacityDrivenTypes =
        ["LABOR_PHYSICIAN", "LABOR_NURSE", "LABOR_TECH", "LABOR_ALLIED", "EQUIPMENT", "ROOM"];

    public static readonly string[] Uoms =
        ["MIN", "HOUR", "DAY", "UNIT", "DOSE", "TXN", "SQM", "CASE", "TEST", "KG", "MEAL", "CYCLE", "HEADCOUNT", "SAR", "WAU"];

    public static readonly string[] ComponentTypes = [.. ResourceTypes, "OVERHEAD"];

    public static string ComponentLabel(string type) =>
        type == "OVERHEAD" ? "Allocated overhead" : ResourceTypeLabel.TryGetValue(type, out var l) ? l : type;

    /// <summary>Labour first, then what the patient consumed, then the estate, overhead last.</summary>
    public static readonly string[] ComponentDisplayOrder =
    [
        "LABOR_PHYSICIAN", "LABOR_NURSE", "LABOR_TECH", "LABOR_ALLIED", "DRUG", "CONSUMABLE", "IMPLANT",
        "BLOOD", "EQUIPMENT", "ROOM", "IT_SERVICE", "UTILITY", "FACILITY", "OVERHEAD",
    ];

    public static readonly string[] CostNatures = ["DIRECT", "INDIRECT", "OVERHEAD"];

    /// <summary>FR-5.6: what a resource type contributes to on a cost card.</summary>
    public static string NatureOf(string resourceType) => resourceType switch
    {
        "EQUIPMENT" or "ROOM" or "UTILITY" or "IT_SERVICE" or "FACILITY" => "INDIRECT",
        _ => "DIRECT",
    };

    public static readonly string[] DriverTypes = ["TIME", "VOLUME", "VALUE", "QUANTITY", "AREA", "HEADCOUNT", "WEIGHTED"];
    public static readonly string[] AllocMethodologies = ["DIRECT", "STEP_DOWN", "RECIPROCAL", "ABC", "TDABC"];
    public static readonly string[] AllocMethods = ["PERCENTAGE", "VOLUME", "TIME", "QUANTITY", "VALUE", "WEIGHTED", "FORMULA"];
    public static readonly string[] Variability = ["FIXED", "VARIABLE", "SEMI_VARIABLE"];
    public static readonly string[] ProcTypes = ["PROCEDURE", "SURGERY", "CONSULT", "LAB", "IMAGING", "BED_DAY", "ER_VISIT", "THERAPY"];
    public static readonly string[] EncTypes = ["IP", "OP", "ER", "DAYCASE"];
    public static readonly string[] PayerTypes = ["GOVERNMENT", "INSURANCE", "CORPORATE", "SELF_PAY"];
    public static readonly string[] PatientSegments = ["PEDIATRIC", "ADULT", "GERIATRIC", "MATERNITY"];
    public static readonly string[] CostTypes = ["STANDARD", "ACTUAL", "BUDGET", "FORECAST", "TARGET"];
    public static readonly string[] RecordStatuses = ["DRAFT", "REVIEW", "APPROVED", "PUBLISHED", "RETIRED"];
    public static readonly string[] VersionStatuses = ["DRAFT", "REVIEW", "FINANCE_APPROVED", "CLINICAL_APPROVED", "PUBLISHED", "RETIRED"];
    public static readonly string[] Baselines = ["STANDARD", "BUDGET", "FORECAST", "TARGET", "PRIOR_PERIOD"];
    public static readonly string[] Directions = ["FAVOURABLE", "UNFAVOURABLE", "NEUTRAL"];

    // ── Runs (FR-2) ───────────────────────────────────────────────────────────────

    public static readonly string[] RunStatuses =
        ["DRAFT", "VALIDATING", "VALIDATED", "CALCULATING", "CALCULATED", "RECONCILED", "REVIEWED", "APPROVED", "PUBLISHED", "FAILED"];

    public static readonly string[] RunTypes = ["STANDARD", "ACTUAL", "BUDGET", "FORECAST", "SIMULATION"];

    /// <summary>FR-2.7: the legal transitions. Anything not listed is rejected by the run service.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RunTransitions = new Dictionary<string, string[]>
    {
        ["DRAFT"] = ["VALIDATING", "FAILED"],
        ["VALIDATING"] = ["VALIDATED", "FAILED"],
        ["VALIDATED"] = ["CALCULATING", "FAILED"],
        ["CALCULATING"] = ["CALCULATED", "FAILED"],
        ["CALCULATED"] = ["RECONCILED", "FAILED"],
        ["RECONCILED"] = ["REVIEWED", "FAILED"],
        ["REVIEWED"] = ["APPROVED", "RECONCILED"],
        ["APPROVED"] = ["PUBLISHED", "REVIEWED"],
        ["PUBLISHED"] = [],
        ["FAILED"] = ["DRAFT"],
    };

    public static bool CanTransition(string from, string to) =>
        RunTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>FR-2.8 / FR-10.7: a published run is immutable on every path.</summary>
    public static bool IsRunImmutable(string status) => status == "PUBLISHED";

    public sealed record RunStepDef(int No, string Code, string Name, bool MovesMoney);

    /// <summary>FR-2.2: the eleven stages in strict order. POOL_BUILD precedes CAPACITY (CR-3).</summary>
    public static readonly RunStepDef[] RunSteps =
    [
        new(1, "VALIDATE", "Validate source data", false),
        new(2, "RESOURCE_RATES", "Resolve effective resource rates", false),
        new(3, "POOL_BUILD", "Build cost pools from the GL", false),
        new(4, "CAPACITY", "Time-driven capacity & unit costs", true),
        new(5, "ALLOCATE_SUPPORT", "Allocate support & overhead", true),
        new(6, "ALLOCATE_ACTIVITY", "Allocate cost centres to activities", true),
        new(7, "COST_STANDARD", "Standard procedure costing", true),
        new(8, "COST_ACTUAL", "Actual & patient-level costing", true),
        new(9, "AGGREGATE", "Package, DRG & profitability roll-up", false),
        new(10, "VARIANCE", "Variance decomposition", false),
        new(11, "RECONCILE", "GL reconciliation", false),
    ];

    public static readonly string[] StepStatuses = ["PENDING", "RUNNING", "SUCCESS", "WARNING", "FAILED"];

    // ── Governance (FR-10) ────────────────────────────────────────────────────────

    public static readonly string[] LifecycleStages =
        ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "CLINICAL_APPROVAL", "PUBLISHED", "REJECTED", "RETIRED"];

    /// <summary>
    /// FR-10.1: clinical approval only where a change alters a clinical assumption; requiring it
    /// everywhere trains people to rubber-stamp.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ApprovalFlow = new Dictionary<string, string[]>
    {
        ["COSTING_VERSION"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "CLINICAL_APPROVAL", "PUBLISHED"],
        ["PROCEDURE_DEFINITION"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "CLINICAL_APPROVAL", "PUBLISHED"],
        ["ACTIVITY"] = ["DRAFT", "REVIEW", "CLINICAL_APPROVAL", "PUBLISHED"],
        ["COST_POOL"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "PUBLISHED"],
        ["ALLOCATION_RULE"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "PUBLISHED"],
        ["COST_DRIVER"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "PUBLISHED"],
        ["RESOURCE_RATE"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "PUBLISHED"],
        ["CALCULATION_RUN"] = ["DRAFT", "REVIEW", "FINANCE_APPROVAL", "PUBLISHED"],
    };

    public static string? NextStage(string entityType, string current)
    {
        if (!ApprovalFlow.TryGetValue(entityType, out var flow)) return null;
        var i = Array.IndexOf(flow, current);
        return i < 0 || i == flow.Length - 1 ? null : flow[i + 1];
    }

    public static readonly string[] AuditActions =
        ["CREATE", "UPDATE", "DELETE", "APPROVE", "REJECT", "PUBLISH", "RUN", "LOGIN", "LOGOUT", "VIEW_PHI", "EXPORT"];

    // ── Residuals (FR-7.3) ────────────────────────────────────────────────────────

    public static readonly string[] ResidualBuckets =
        ["UNUSED_CAPACITY", "OVER_CAPACITY", "NON_PATIENT_ACTIVITY", "UNMAPPED_GL", "NO_DRIVER_VOLUME", "ROUNDING"];

    public static readonly IReadOnlyDictionary<string, string> ResidualLabel = new Dictionary<string, string>
    {
        ["UNUSED_CAPACITY"] = "Cost of unused capacity",
        ["OVER_CAPACITY"] = "Capacity consumed beyond the practical model",
        ["NON_PATIENT_ACTIVITY"] = "Non-patient activity (teaching, research, admin)",
        ["UNMAPPED_GL"] = "GL postings with no cost-pool mapping",
        ["NO_DRIVER_VOLUME"] = "Pools whose driver measured zero volume",
        ["ROUNDING"] = "Rounding",
    };

    public static readonly IReadOnlyDictionary<string, string> ResidualOwner = new Dictionary<string, string>
    {
        ["UNUSED_CAPACITY"] = "Operations / workforce planning",
        ["OVER_CAPACITY"] = "Operations",
        ["NON_PATIENT_ACTIVITY"] = "Supply chain / clinical directorates",
        ["UNMAPPED_GL"] = "Finance",
        ["NO_DRIVER_VOLUME"] = "Costing",
        ["ROUNDING"] = "Nobody; reported for completeness",
    };

    public static readonly string[] Severities = ["BLOCKING", "ERROR", "WARNING", "INFO"];

    // ── Simulation (FR-8.4) ───────────────────────────────────────────────────────

    public static readonly IReadOnlyDictionary<string, string> LeverLabel = new Dictionary<string, string>
    {
        ["RESOURCE_RATE"] = "Resource rate (salary / price)",
        ["RESOURCE_CAPACITY"] = "Resource capacity (available minutes)",
        ["ACTIVITY_DURATION"] = "Activity duration",
        ["DRIVER_VOLUME"] = "Cost driver volume",
        ["PROCEDURE_VOLUME"] = "Procedure volume",
        ["PATIENT_VOLUME"] = "Patient volume",
        ["LOS"] = "Length of stay",
        ["OCCUPANCY"] = "Occupancy",
        ["REIMBURSEMENT"] = "Payer reimbursement",
        ["OVERHEAD_RATE"] = "Overhead allocation rate",
        ["DRUG_PRICE"] = "Drug price",
        ["CONSUMABLE_PRICE"] = "Consumable price",
        ["EQUIPMENT_COST"] = "Equipment cost",
        ["STAFF_UTILISATION"] = "Staff utilisation",
    };

    public static readonly string[] LeverTypes = [.. LeverLabel.Keys];
    public static readonly string[] LeverTargetTypes = ["RESOURCE", "RESOURCE_TYPE", "ACTIVITY", "PROCEDURE", "PAYER", "DEPARTMENT", "ALL"];
    public static readonly string[] ChangeTypes = ["PCT", "ABS", "SET"];
    public static readonly string[] ScenarioStatuses = ["DRAFT", "RUNNING", "COMPLETED", "FAILED", "ARCHIVED"];

    /// <summary>Column -> legal values, read by the database check-constraint generator.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ColumnDomains = new Dictionary<string, string[]>
    {
        ["Department.DeptType"] = DeptTypes,
        ["CostCenter.CcType"] = CcTypes,
        ["GLAccount.NaturalClass"] = NaturalClasses,
        ["GLAccount.CostClass"] = CostClasses,
        ["Resource.ResourceType"] = ResourceTypes,
        ["CostDriver.DriverType"] = DriverTypes,
        ["CostDriver.AllocMethodology"] = AllocMethodologies,
        ["CostPool.AllocMethod"] = AllocMethodologies,
        ["AllocationRule.Method"] = AllocMethods,
        ["AllocationRule.Methodology"] = AllocMethodologies,
        ["Activity.Variability"] = Variability,
        ["Procedure.ProcType"] = ProcTypes,
        ["Encounter.EncType"] = EncTypes,
        ["Payer.PayerType"] = PayerTypes,
        ["Patient.Segment"] = PatientSegments,
        ["CostingVersion.Status"] = VersionStatuses,
        ["CalculationRun.Status"] = RunStatuses,
        ["CalculationRun.RunType"] = RunTypes,
        ["RunStep.Status"] = StepStatuses,
        ["ProcedureCost.CostType"] = CostTypes,
        ["CostComponent.ComponentType"] = ComponentTypes,
        ["CostComponent.CostNature"] = CostNatures,
        ["ResidualBucket.Bucket"] = ResidualBuckets,
        ["Variance.Baseline"] = Baselines,
        ["Variance.Direction"] = Directions,
        ["DataQualityIssue.Severity"] = Severities,
        ["ApprovalRequest.Stage"] = LifecycleStages,
        ["AuditLog.Action"] = AuditActions,
        ["ScenarioLever.LeverType"] = LeverTypes,
        ["ScenarioLever.ChangeType"] = ChangeTypes,
        ["Scenario.Status"] = ScenarioStatuses,
    };
}
