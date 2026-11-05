namespace Verita.Domain.Entities;

// Sixty entities (BRD section 18). Money is decimal and maps to DECIMAL(19,4); properties marked
// [Precise] are rates and quantities at DECIMAL(19,6); [Share] marks percentages, shares and
// weights at DECIMAL(9,6). Effective dates are DateOnly (SQL DATE); timestamps are DateTime.

/// <summary>DECIMAL(19,6): rates and quantities.</summary>
[AttributeUsage(AttributeTargets.Property)] public sealed class PreciseAttribute : Attribute;
/// <summary>DECIMAL(9,6): percentages, shares, weights.</summary>
[AttributeUsage(AttributeTargets.Property)] public sealed class ShareAttribute : Attribute;

public interface IEffectiveDated
{
    DateOnly EffectiveFrom { get; }
    DateOnly? EffectiveTo { get; }
}

public static class EffectiveDating
{
    /// <summary>True when the row applies on the given as-of date.</summary>
    public static bool IsEffective(this IEffectiveDated e, DateOnly asOf) =>
        e.EffectiveFrom <= asOf && (e.EffectiveTo is null || e.EffectiveTo >= asOf);
}

// ═══ A. Organisation ═══════════════════════════════════════════════════════════════

public class Hospital
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Currency { get; set; } = "SAR";
    public int BedCount { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>HISGENX SetupID. Not unique: the same HIS tenant can map to more than one VERITA hospital.</summary>
    public string? HisSetupId { get; set; }
    /// <summary>HISGENX ProjectID (branch). Paired with HisSetupId; not unique.</summary>
    public int? HisProjectId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Facility
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string FacilityType { get; set; } = "";
    [Precise] public decimal SquareMetres { get; set; }
}

public class ServiceLine
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

public class Department
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? ServiceLineId { get; set; }
    public string Code { get; set; } = "";
    public string CatalogCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeptType { get; set; } = "";
    public string? Specialty { get; set; }
}

public class CostCenter
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? FacilityId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CcType { get; set; } = "";
    /// <summary>Lower closes first in step-down: the most-serving centres first.</summary>
    public int StepDownSeq { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}

// ═══ B. Financial structure ════════════════════════════════════════════════════════

public class GLAccount
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NaturalClass { get; set; } = "";
    public string CostClass { get; set; } = "";
}

public class Period
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int Days { get; set; }
    public string Status { get; set; } = "OPEN";
    /// <summary>The hospital this period belongs to. Null only while a fixture loads, before AssignSharedAsync.</summary>
    public Guid? HospitalId { get; set; }
}

public class GLPosting
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid GLAccountId { get; set; }
    public Guid CostCenterId { get; set; }
    public decimal Amount { get; set; }
    public string SourceSystem { get; set; } = "ERP-GL";
    public string SourceRef { get; set; } = "";
    public DateTime PostedAt { get; set; }
}

public class CostPool : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid CostCenterId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Classification { get; set; } = "";
    public string AllocMethod { get; set; } = "";
    public Guid? PrimaryDriverId { get; set; }
    public string Status { get; set; } = "PUBLISHED";
    public int Version { get; set; } = 1;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class GLMapping : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid GLAccountId { get; set; }
    public Guid CostCenterId { get; set; }
    public Guid CostPoolId { get; set; }
    [Share] public decimal Weight { get; set; } = 1m;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

// ═══ C. Drivers ════════════════════════════════════════════════════════════════════

public class CostDriver : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string CatalogCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public string DriverType { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string SourceField { get; set; } = "";
    /// <summary>The derivation in words — what lets a volume be checked at source (FR-1.12).</summary>
    public string Formula { get; set; } = "";
    public string Frequency { get; set; } = "MONTHLY";
    public Guid? CostCenterId { get; set; }
    public Guid? CostPoolId { get; set; }
    public Guid? ActivityId { get; set; }
    public string AllocMethodology { get; set; } = "";
    public string Status { get; set; } = "PUBLISHED";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class DriverVolume
{
    public Guid Id { get; set; }
    public Guid DriverId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid CostCenterId { get; set; }
    public Guid? ActivityId { get; set; }
    [Precise] public decimal Quantity { get; set; }
    public string SourceSystem { get; set; } = "EDW";
}

// ═══ D. Resources ══════════════════════════════════════════════════════════════════

public class Resource
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid CostCenterId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string Uom { get; set; } = "";
    public bool IsCapacityDriven { get; set; }
    /// <summary>FR-1.17: the pool that funds this resource; what bounds the consumption stream.</summary>
    public Guid? CostPoolId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ResourceRate : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    [Precise] public decimal UnitCost { get; set; }
    public string Uom { get; set; } = "";
    public string Basis { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "PUBLISHED";
}

public class ResourceCapacity : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    [Precise] public decimal TheoreticalMinutes { get; set; }
    [Share] public decimal PracticalRatio { get; set; } = 0.85m;
    [Precise] public decimal PracticalMinutes { get; set; }
    public decimal AnnualCost { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

/// <summary>FR-1.18: a named physician pointing at a rate-bearing role resource.</summary>
public class Provider
{
    public Guid Id { get; set; }
    /// <summary>The role resource carrying the rate; null is a physician not assigned to one.</summary>
    public Guid? ResourceId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string Grade { get; set; } = "";
    /// <summary>A HIS placeholder (HIS_Doctor.IsDoctorDummy) billing for a clinic or several, not a person.</summary>
    public bool IsDummy { get; set; }
}

/// <summary>A clinic a doctor works in, as HIS holds it (HIS_DoctorClinic): a doctor has many clinics and a clinic many doctors.</summary>
public class ProviderClinic
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid DepartmentId { get; set; }
    public string HisClinicId { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

// ═══ E. Service catalogue ══════════════════════════════════════════════════════════

public class Activity : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string CatalogCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid CostCenterId { get; set; }
    public string? Specialty { get; set; }
    [Precise] public decimal StdDuration { get; set; }
    [Precise] public decimal MinDuration { get; set; }
    [Precise] public decimal MaxDuration { get; set; }
    public string Variability { get; set; } = "";
    public string Directness { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "PUBLISHED";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class ActivityResource
{
    public Guid Id { get; set; }
    public Guid ActivityId { get; set; }
    public Guid ResourceId { get; set; }
    [Precise] public decimal StdQuantity { get; set; }
    public string Uom { get; set; } = "";
}

public class ActivityDriver
{
    public Guid Id { get; set; }
    public Guid ActivityId { get; set; }
    public Guid DriverId { get; set; }
    [Precise] public decimal Quantity { get; set; }
    [Share] public decimal Weight { get; set; } = 1m;
}

public class Procedure
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string CatalogCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>HIS classifies a procedure at three levels (HIS_Procedure): SubGroup, Group and Category. Category's name is <see cref="Category"/>.</summary>
    public string? HisCategoryId { get; set; }
    public string? HisGroupId { get; set; }
    public string? GroupName { get; set; }
    public string? HisSubGroupId { get; set; }
    public string? SubGroupName { get; set; }
    public bool IsPackage { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? ServiceLineId { get; set; }
    public string ProcType { get; set; } = "";
    public int Complexity { get; set; } = 3;
    public bool IsActive { get; set; } = true;
}

public class ProcedureRate : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid ProcedureId { get; set; }
    public Guid HospitalId { get; set; }
    public decimal ListPrice { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

/// <summary>Configured margin on a procedure/activity type for one hospital, costing version and stream.</summary>
public class ProcedureTypeCosting
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid CostingVersionId { get; set; }
    public string ProcType { get; set; } = "";
    public string Stream { get; set; } = "DIRECT";
    [Share] public decimal MarginPct { get; set; }
    /// <summary>The ZERO_FIXED stream only: a fixed cost in SAR, since no % of a zero price can be one.</summary>
    public decimal? FixedAmount { get; set; }
}

/// <summary>Configured margin on one procedure/activity; overrides the type margin when set.</summary>
public class ProcedureMargin
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid CostingVersionId { get; set; }
    public Guid ProcedureId { get; set; }
    public string Stream { get; set; } = "DIRECT";
    [Share] public decimal MarginPct { get; set; }
    /// <summary>The ZERO_FIXED stream only: a fixed cost in SAR.</summary>
    public decimal? FixedAmount { get; set; }
}

/// <summary>Indirect overhead applied to every encounter billed at a cost centre.</summary>
public class EncounterOverhead
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid CostingVersionId { get; set; }
    public Guid CostCenterId { get; set; }
    public string Description { get; set; } = "";
    [Share] public decimal OverheadPct { get; set; }
    public decimal OverheadAmount { get; set; }
}

public class CostingVersion : IEffectiveDated
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateTime? PublishedAt { get; set; }
    public Guid? PublishedById { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>FR-1.20: the versioned binding of a procedure to its recipe. Never overwritten.</summary>
public class CostingDefinition
{
    public Guid Id { get; set; }
    public Guid ProcedureId { get; set; }
    public Guid CostingVersionId { get; set; }
    public string? Notes { get; set; }
}

public class ProcedureActivityLine
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid ActivityId { get; set; }
    [Precise] public decimal Quantity { get; set; }
}

public class ProcedureResourceLine
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid ResourceId { get; set; }
    [Precise] public decimal Quantity { get; set; }
    public string Uom { get; set; } = "";
}

public class Package
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public string Code { get; set; } = "";
    public string CatalogCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public int CoveredLOS { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PackageComponent
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public Guid ProcedureId { get; set; }
    [Precise] public decimal Quantity { get; set; }
}

public class Drg
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mdc { get; set; } = "";
    [Precise] public decimal RelativeWt { get; set; }
    [Precise] public decimal ExpectedLOS { get; set; }
    public decimal BaseRate { get; set; }
}

// ═══ G. Rules and versions ═════════════════════════════════════════════════════════

public class AllocationRule : IEffectiveDated
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid SourcePoolId { get; set; }
    public Guid? DriverId { get; set; }
    public string Method { get; set; } = "";
    public string Methodology { get; set; } = "";
    /// <summary>1 reciprocal, 2 step-down, 3 cost centre to activity (FR-3.2).</summary>
    public int Stage { get; set; }
    public int Sequence { get; set; } = 100;
    /// <summary>FR-3.9: fraction of the pool this rule moves; rules on one pool sum to 1.</summary>
    [Share] public decimal PoolShare { get; set; } = 1m;
    public string? Formula { get; set; }
    public string Status { get; set; } = "PUBLISHED";
    public int Version { get; set; } = 1;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public class AllocationRuleTarget
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string TargetType { get; set; } = "";
    public Guid? CostCenterId { get; set; }
    [Share] public decimal? Percentage { get; set; }
    [Share] public decimal Weight { get; set; } = 1m;
}
