namespace Verita.Domain.Entities;

// ═══ H. Runs and results ═══════════════════════════════════════════════════════════

public class CalculationRun
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid HospitalId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid CostingVersionId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string RunType { get; set; } = "ACTUAL";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationMs { get; set; }
    public string? MetricsJson { get; set; }
    /// <summary>True when the run was forced past ERROR issues and so can never publish (FR-9.8).</summary>
    public bool Forced { get; set; }
    public string TriggeredBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RunStep
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int StepNo { get; set; }
    public string StepCode { get; set; } = "";
    public string StepName { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationMs { get; set; }
    public int RowsWritten { get; set; }
    /// <summary>FR-2.4: money in and out — how a conservation breach is located.</summary>
    public decimal MoneyIn { get; set; }
    public decimal MoneyOut { get; set; }
    public string? Message { get; set; }
    public string? MetricsJson { get; set; }
}

public class ResourceUnitCost
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ResourceId { get; set; }
    public decimal AnnualCost { get; set; }
    public decimal PeriodCost { get; set; }
    [Precise] public decimal TheoreticalMinutes { get; set; }
    [Precise] public decimal PracticalMinutes { get; set; }
    [Precise] public decimal AvailableMinutes { get; set; }
    [Precise] public decimal UsedMinutes { get; set; }
    [Precise] public decimal UnusedMinutes { get; set; }
    [Precise] public decimal UnitCost { get; set; }
    public string Uom { get; set; } = "";
    public decimal CostOfUsedCapacity { get; set; }
    public decimal CostOfUnusedCapacity { get; set; }
    [Precise] public decimal UtilisationPct { get; set; }
    public string CalcExpression { get; set; } = "";
}

public class CostPoolBalance
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid CostPoolId { get; set; }
    public decimal DirectAmount { get; set; }
    public decimal ReceivedAmount { get; set; }
    public decimal AllocatedOut { get; set; }
    public decimal Residual { get; set; }
    public bool IsClosed { get; set; }
}

public class CostCenterBalance
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid CostCenterId { get; set; }
    public decimal DirectAmount { get; set; }
    public decimal ReceivedAmount { get; set; }
    public decimal AllocatedOut { get; set; }
    public decimal FinalAmount { get; set; }
    public int StepDownSeq { get; set; } = 100;
    public bool IsClosed { get; set; }
}

public class AllocationResult
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? RuleId { get; set; }
    public Guid? SourcePoolId { get; set; }
    public Guid? SourceCostCenterId { get; set; }
    public string TargetType { get; set; } = "";
    public Guid? TargetCostCenterId { get; set; }
    public Guid? TargetActivityId { get; set; }
    public string TargetLabel { get; set; } = "";
    public int Stage { get; set; }
    /// <summary>FR-3.6: iterations used, on the one row per (rule, target).</summary>
    public int Iteration { get; set; }
    public Guid? DriverId { get; set; }
    [Precise] public decimal DriverQty { get; set; }
    [Precise] public decimal DriverVolume { get; set; }
    [Precise] public decimal DriverShare { get; set; }
    public decimal Amount { get; set; }
    public string CalcExpression { get; set; } = "";
}

public class ActivityCost
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ActivityId { get; set; }
    [Precise] public decimal Volume { get; set; }
    public decimal DirectCost { get; set; }
    public decimal IndirectCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal TotalCost { get; set; }
    [Precise] public decimal UnitCost { get; set; }
    public string CalcExpression { get; set; } = "";
}

public class ProcedureCost
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ProcedureId { get; set; }
    public string CostType { get; set; } = "";
    [Precise] public decimal Volume { get; set; }
    public decimal DirectCost { get; set; }
    public decimal IndirectCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal TotalCost { get; set; }
    [Precise] public decimal UnitCost { get; set; }
    public decimal Revenue { get; set; }
    public decimal ContributionMargin { get; set; }
    public decimal OperatingMargin { get; set; }
}

public class EncounterCost
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid EncounterId { get; set; }
    public decimal DirectCost { get; set; }
    public decimal IndirectCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal TotalCost { get; set; }
    public decimal StandardCost { get; set; }
    public decimal Revenue { get; set; }
    public decimal ContributionMargin { get; set; }
    public decimal OperatingMargin { get; set; }
    [Precise] public decimal MarginPct { get; set; }
    public decimal CostPerDay { get; set; }
    public int ServiceCount { get; set; }
    /// <summary>FR-5.9: weighted activity units for administration overhead.</summary>
    [Precise] public decimal WeightedActivityUnits { get; set; }
}

public class PackageCost
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid PackageId { get; set; }
    public int CaseCount { get; set; }
    public decimal StandardCost { get; set; }
    public decimal ActualCost { get; set; }
    public decimal Revenue { get; set; }
    public decimal Variance { get; set; }
    [Precise] public decimal VariancePct { get; set; }
    public decimal ContributionMargin { get; set; }
    public decimal OperatingMargin { get; set; }
    [Precise] public decimal MarginPct { get; set; }
}

public class DrgCostSummary
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid DrgId { get; set; }
    public int CaseCount { get; set; }
    public decimal AvgCost { get; set; }
    public decimal MedianCost { get; set; }
    public decimal P25Cost { get; set; }
    public decimal P75Cost { get; set; }
    public decimal P90Cost { get; set; }
    public decimal MinCost { get; set; }
    public decimal MaxCost { get; set; }
    public decimal StdDevCost { get; set; }
    [Precise] public decimal AvgLOS { get; set; }
    public decimal CostPerDay { get; set; }
    public decimal AvgReimbursement { get; set; }
    public decimal AvgMargin { get; set; }
    [Precise] public decimal MarginPct { get; set; }
    [Precise] public decimal VariationCoeff { get; set; }
    public int OutlierCount { get; set; }
}

/// <summary>The traceability spine: every currency unit that lands anywhere is one row.</summary>
public class CostComponent
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? CostingVersionId { get; set; }
    public string Scope { get; set; } = "";
    public Guid ScopeId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? ProcedureId { get; set; }
    public Guid? ActivityId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? CostPoolId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? AllocationRuleId { get; set; }
    public string ComponentType { get; set; } = "";
    public string CostNature { get; set; } = "";
    public string Label { get; set; } = "";
    [Precise] public decimal Quantity { get; set; }
    public string Uom { get; set; } = "";
    [Precise] public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    /// <summary>The arithmetic, in words. Never empty (check constraint).</summary>
    public string CalcExpression { get; set; } = "";
    public string? SourceSystem { get; set; }
    public string? SourceRef { get; set; }
    public DateTime CalculatedAt { get; set; }
}

/// <summary>Money that legitimately did not reach a patient — always named, never swept.</summary>
public class ResidualBucket
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Bucket { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
    public string? DetailJson { get; set; }
    public string Explanation { get; set; } = "";
}

// ═══ I. Analysis ═══════════════════════════════════════════════════════════════════

public class Variance
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Scope { get; set; } = "";
    public Guid ScopeId { get; set; }
    public string ScopeLabel { get; set; } = "";
    public string Baseline { get; set; } = "STANDARD";
    public decimal ActualAmount { get; set; }
    public decimal BaselineAmount { get; set; }
    public decimal VarianceAmount { get; set; }
    [Precise] public decimal VariancePct { get; set; }
    public string Direction { get; set; } = "";
    public int CaseCount { get; set; }
    public string Summary { get; set; } = "";
}

public class VarianceComponent
{
    public Guid Id { get; set; }
    public Guid VarianceId { get; set; }
    public string ComponentType { get; set; } = "";
    public string Label { get; set; } = "";
    [Precise] public decimal ActualQty { get; set; }
    [Precise] public decimal StandardQty { get; set; }
    [Precise] public decimal ActualRate { get; set; }
    [Precise] public decimal StandardRate { get; set; }
    public decimal RateEffect { get; set; }
    public decimal QuantityEffect { get; set; }
    public decimal MixEffect { get; set; }
    public decimal TotalEffect { get; set; }
    public string Explanation { get; set; } = "";
}

public class Reconciliation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public decimal GlTotal { get; set; }
    public decimal PatientCost { get; set; }
    public decimal ResidualTotal { get; set; }
    public decimal Unexplained { get; set; }
    public decimal Materiality { get; set; }
    [Precise] public decimal AttributionPct { get; set; }
    public string Status { get; set; } = "";
    public string Narrative { get; set; } = "";
}

public class ReconciliationLine
{
    public Guid Id { get; set; }
    public Guid ReconciliationId { get; set; }
    public int Sequence { get; set; }
    public string Label { get; set; } = "";
    public string LineType { get; set; } = "";
    public string? Bucket { get; set; }
    public decimal Amount { get; set; }
    /// <summary>FR-7.6: the running balance after this line.</summary>
    public decimal RunningBalance { get; set; }
    public string Explanation { get; set; } = "";
}

public class Scenario
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid BaseRunId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
}

public class ScenarioLever
{
    public Guid Id { get; set; }
    public Guid ScenarioId { get; set; }
    public string LeverType { get; set; } = "";
    public string TargetType { get; set; } = "";
    /// <summary>An entity id, or a resource type code when TargetType is RESOURCE_TYPE.</summary>
    public string? TargetId { get; set; }
    public string TargetLabel { get; set; } = "";
    public string ChangeType { get; set; } = "";
    [Precise] public decimal Value { get; set; }
    public string? Note { get; set; }
}

public class ScenarioResult
{
    public Guid Id { get; set; }
    public Guid ScenarioId { get; set; }
    public string Dimension { get; set; } = "";
    public string? DimensionId { get; set; }
    public string Label { get; set; } = "";
    public decimal BaselineCost { get; set; }
    public decimal ScenarioCost { get; set; }
    public decimal CostDelta { get; set; }
    [Precise] public decimal CostDeltaPct { get; set; }
    public decimal BaselineRevenue { get; set; }
    public decimal ScenarioRevenue { get; set; }
    public decimal RevenueDelta { get; set; }
    public decimal BaselineMargin { get; set; }
    public decimal ScenarioMargin { get; set; }
    public decimal MarginDelta { get; set; }
    public string Explanation { get; set; } = "";
}

public class DataQualityIssue
{
    public Guid Id { get; set; }
    public Guid? RunId { get; set; }
    public string CheckCode { get; set; } = "";
    public string CheckName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string EntityLabel { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Value { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Status { get; set; } = "OPEN";
}
