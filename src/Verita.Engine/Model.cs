using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine;

// The engine's inputs. A RunContext is loaded once per run by the infrastructure layer and
// passed by reference through every stage (ARCH-5). Nothing in a stage queries anything: a
// stage that needs a rate, a driver volume or a recipe reads it from here. That is what makes
// the engine testable without a database or a host (ARCH-1, NFR-21).

public sealed record CcInfo(Guid Id, string Code, string Name, string CcType, int StepDownSeq, Guid DepartmentId);

public sealed record PoolInfo(
    Guid Id, string Code, string Name, Guid CostCenterId, string Classification, string AllocMethod,
    Guid? PrimaryDriverId,
    /// <summary>CR-4: true when no resource draws from this pool, so its money is allocated by rule.</summary>
    bool IsAllocationStream);

public sealed record CapacityInfo(decimal TheoreticalMinutes, decimal PracticalMinutes, decimal PracticalRatio, decimal AnnualCost);

public sealed record ResInfo(
    Guid Id, string Code, string Name, string ResourceType, string Uom, bool IsCapacityDriven,
    Guid CostCenterId, Guid? CostPoolId,
    /// <summary>Effective unit cost at the run's as-of date, after any scenario override.</summary>
    decimal Rate, Guid? RateId, CapacityInfo? Capacity);

public sealed record DriverInfo(Guid Id, string Code, string CatalogCode, string Name, string Unit, string DriverType, string Formula);

public sealed record RuleTargetInfo(Guid? CostCenterId, decimal? Percentage, decimal Weight);

public sealed record RuleInfo(
    Guid Id, string Code, string Name, Guid SourcePoolId, Guid SourceCostCenterId, Guid? DriverId,
    string Method, string Methodology, int Stage, int Sequence, decimal PoolShare, string? Formula,
    IReadOnlyList<RuleTargetInfo> Targets);

public sealed record RecipeLine(Guid ResourceId, decimal StdQuantity, string Uom);

public sealed record ActInfo(
    Guid Id, string Code, string CatalogCode, string Name, string Category, Guid CostCenterId, Guid DepartmentId,
    string Directness, decimal StdDuration, decimal MinDuration, decimal MaxDuration,
    IReadOnlyList<RecipeLine> Recipe, Guid? DriverId);

public sealed record ProcActivityLine(Guid ActivityId, decimal Quantity);
public sealed record ProcResourceLine(Guid ResourceId, decimal Quantity, string Uom);

public sealed record ProcInfo(
    Guid Id, string Code, string CatalogCode, string Name, string ProcType, string Category, int Complexity,
    Guid DepartmentId, Guid? ServiceLineId, decimal ListPrice,
    /// <summary>Recipe under the run's costing version (FR-5.3); empty when none is defined.</summary>
    IReadOnlyList<ProcActivityLine> ActivityLines, IReadOnlyList<ProcResourceLine> ResourceLines);

public sealed record PackageInfo(Guid Id, string Code, string Name, decimal Price, IReadOnlyList<(Guid ProcedureId, decimal Quantity)> Components);

public sealed record PostingRow(Guid GlAccountId, string AccountCode, string AccountName, Guid CostCenterId, decimal Amount);
public sealed record MappingRow(Guid GlAccountId, Guid CostCenterId, Guid CostPoolId, decimal Weight, string AccountLabel, string CostCenterName);
public sealed record EncounterRow(Guid Id, int LengthOfStay, Guid PayerId, Guid? DrgId, Guid? PackageId, bool IsSurgical);
public sealed record ServiceRow(Guid Id, Guid EncounterId, Guid ProcedureId, Guid CostCenterId, Guid? ProviderId, decimal Quantity);
public sealed record RevenueRow(Guid EncounterId, Guid? ProcedureId, Guid PayerId, decimal NetAmount);
public sealed record RateSpan(Guid ResourceId, DateOnly From, DateOnly? To);

/// <summary>One measured transaction, streamed once (FR-2.10).</summary>
public readonly record struct TxnRow(
    Guid EncounterId, Guid? EncounterServiceId, Guid ActivityId, Guid ResourceId, Guid CostCenterId,
    decimal Quantity, string Uom, string SourceRef);

public sealed class RunContext
{
    public required Guid RunId { get; init; }
    public required string RunCode { get; init; }
    public required Guid HospitalId { get; init; }
    public required string HospitalCode { get; init; }
    public required string HospitalName { get; init; }
    public required Guid PeriodId { get; init; }
    public required string PeriodCode { get; init; }
    public required int PeriodDays { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }
    /// <summary>FR-1.23: every effective-dated input is resolved at the period end.</summary>
    public DateOnly AsOf => PeriodEnd;
    public required Guid CostingVersionId { get; init; }
    public required string CostingVersionCode { get; init; }
    public DateTime CalculatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Scale at which distributions are rounded (CR-2). Four, the storage scale, in normal use;
    /// two reproduces the reference implementation's halala rounding path for parity diagnosis.
    /// </summary>
    public int DistributionScale { get; init; } = Money.StorageScale;

    public required IReadOnlyList<CcInfo> CostCenters { get; init; }
    public required IReadOnlyList<PoolInfo> Pools { get; init; }
    public required IReadOnlyDictionary<Guid, ResInfo> Resources { get; init; }
    public required IReadOnlyDictionary<Guid, DriverInfo> Drivers { get; init; }
    public required IReadOnlyDictionary<(Guid DriverId, Guid CostCenterId), decimal> DriverVolumes { get; init; }
    public required IReadOnlyList<RuleInfo> Rules { get; init; }
    public required IReadOnlyDictionary<Guid, ActInfo> Activities { get; init; }
    public required IReadOnlyDictionary<Guid, ProcInfo> Procedures { get; init; }
    public required IReadOnlyDictionary<Guid, PackageInfo> Packages { get; init; }

    public required IReadOnlyList<PostingRow> Postings { get; init; }
    public required IReadOnlyList<MappingRow> Mappings { get; init; }
    public required IReadOnlyList<EncounterRow> Encounters { get; init; }
    public required IReadOnlyList<ServiceRow> Services { get; init; }
    public required IReadOnlyList<RevenueRow> Revenue { get; init; }
    /// <summary>Every rate span for the hospital's resources, for the overlap check.</summary>
    public required IReadOnlyList<RateSpan> RateSpans { get; init; }

    public ScenarioOverrides Overrides { get; init; } = new();

    private Dictionary<Guid, CcInfo>? _ccById;
    private Dictionary<Guid, PoolInfo>? _poolById;
    public IReadOnlyDictionary<Guid, CcInfo> CcById => _ccById ??= CostCenters.ToDictionary(c => c.Id);
    public IReadOnlyDictionary<Guid, PoolInfo> PoolById => _poolById ??= Pools.ToDictionary(p => p.Id);

    public decimal DriverVolume(Guid driverId, Guid ccId) => DriverVolumes.TryGetValue((driverId, ccId), out var v) ? v : 0m;
}

/// <summary>
/// Lever overrides applied by a simulation (FR-8.1). A real run carries the empty set; a
/// scenario is the same pipeline with these multipliers applied at load and in the usage pass.
/// </summary>
public sealed class ScenarioOverrides
{
    public Dictionary<Guid, decimal> ResourceRate { get; } = [];
    public Dictionary<Guid, decimal> ResourceCapacity { get; } = [];
    public Dictionary<Guid, decimal> ActivityDuration { get; } = [];
    public Dictionary<Guid, decimal> ProcedureVolume { get; } = [];
    public decimal LosFactor { get; set; } = 1m;
    public decimal PatientVolume { get; set; } = 1m;
    public Dictionary<Guid, decimal> Reimbursement { get; } = [];
    public decimal OverheadRate { get; set; } = 1m;

    public bool IsEmpty =>
        ResourceRate.Count == 0 && ResourceCapacity.Count == 0 && ActivityDuration.Count == 0 &&
        ProcedureVolume.Count == 0 && LosFactor == 1m && PatientVolume == 1m && Reimbursement.Count == 0 && OverheadRate == 1m;

    public decimal Get(Dictionary<Guid, decimal> map, Guid id) => map.TryGetValue(id, out var m) ? m : 1m;
}

/// <summary>What a stage reports on its RunStep (FR-2.4).</summary>
public sealed record StepResult(
    int RowsWritten, decimal MoneyIn, decimal MoneyOut, string Status = "SUCCESS", string? Message = null,
    IReadOnlyDictionary<string, object?>? Metrics = null);

/// <summary>The rows a stage produces, handed to the writer that persists them (ARCH-4).</summary>
public sealed class StageOutput
{
    public List<DataQualityIssue> Issues { get; } = [];
    public List<CostPoolBalance> PoolBalances { get; } = [];
    public List<ResourceUnitCost> UnitCosts { get; } = [];
    public List<CostCenterBalance> CcBalances { get; } = [];
    public List<AllocationResult> Allocations { get; } = [];
    public List<ActivityCost> ActivityCosts { get; } = [];
    public List<ProcedureCost> ProcedureCosts { get; } = [];
    public List<EncounterCost> EncounterCosts { get; } = [];
    public List<CostComponent> Components { get; } = [];
    public List<PackageCost> PackageCosts { get; } = [];
    public List<DrgCostSummary> DrgSummaries { get; } = [];
    public List<Variance> Variances { get; } = [];
    public List<VarianceComponent> VarianceComponents { get; } = [];
    public List<ResidualBucket> Residuals { get; } = [];
    public List<Reconciliation> Reconciliations { get; } = [];
    public List<ReconciliationLine> ReconciliationLines { get; } = [];

    public int RowCount =>
        Issues.Count + PoolBalances.Count + UnitCosts.Count + CcBalances.Count + Allocations.Count + ActivityCosts.Count +
        ProcedureCosts.Count + EncounterCosts.Count + Components.Count + PackageCosts.Count + DrgSummaries.Count +
        Variances.Count + VarianceComponents.Count + Residuals.Count + Reconciliations.Count + ReconciliationLines.Count;
}

public sealed class RunFailedException(string message, string? stepCode = null) : Exception(message)
{
    public string? StepCode { get; } = stepCode;
}
