namespace Verita.Domain.Entities;

// ═══ F. Clinical transactions ══════════════════════════════════════════════════════

public class Payer
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string PayerType { get; set; } = "";
    /// <summary>Contracted reimbursement as a fraction of list price.</summary>
    [Share] public decimal ContractRate { get; set; } = 1m;
}

public class Patient
{
    public Guid Id { get; set; }
    public string Mrn { get; set; } = "";
    public Guid HospitalId { get; set; }
    /// <summary>Synthetic. No real patient data exists in the fixture.</summary>
    public string FullName { get; set; } = "";
    public int BirthYear { get; set; }
    public string Sex { get; set; } = "";
    public string Segment { get; set; } = "";
    public string NationalIdMasked { get; set; } = "";
}

public class Encounter
{
    public Guid Id { get; set; }
    public string EncounterNo { get; set; } = "";
    public Guid PatientId { get; set; }
    public Guid HospitalId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? ServiceLineId { get; set; }
    public Guid PayerId { get; set; }
    public Guid? DrgId { get; set; }
    public Guid? PackageId { get; set; }
    public Guid? ProviderId { get; set; }
    public string EncType { get; set; } = "";
    public DateTime AdmitAt { get; set; }
    public DateTime? DischargeAt { get; set; }
    public int LengthOfStay { get; set; }
    public bool IsSurgical { get; set; }
    public string Acuity { get; set; } = "ROUTINE";
}

public class EncounterService
{
    public Guid Id { get; set; }
    public Guid EncounterId { get; set; }
    public Guid ProcedureId { get; set; }
    public Guid CostCenterId { get; set; }
    public Guid? ProviderId { get; set; }
    [Precise] public decimal Quantity { get; set; } = 1m;
    public DateTime PerformedAt { get; set; }
    public string SourceSystem { get; set; } = "VIDA-HIS";
    public string SourceRef { get; set; } = "";
}

/// <summary>Measured resource consumption — the actual-cost source of truth.</summary>
public class ActivityTransaction
{
    public Guid Id { get; set; }
    public Guid EncounterId { get; set; }
    public Guid? EncounterServiceId { get; set; }
    public Guid ActivityId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid CostCenterId { get; set; }
    [Precise] public decimal Quantity { get; set; }
    public string Uom { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string SourceSystem { get; set; } = "";
    /// <summary>CR-10: fully qualified, including the activity.</summary>
    public string SourceRef { get; set; } = "";
}

public class RevenueTransaction
{
    public Guid Id { get; set; }
    public Guid EncounterId { get; set; }
    public Guid? ProcedureId { get; set; }
    public Guid PayerId { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal Discount { get; set; }
    public decimal NetAmount { get; set; }
    public DateTime PostedAt { get; set; }
    public string SourceSystem { get; set; } = "CYCLUS-RCM";
    public string SourceRef { get; set; } = "";
}

// ═══ Staging contract (BRD 22.2) ═══════════════════════════════════════════════════

/// <summary>Every inbound load, with accepted and rejected counts.</summary>
public class ImportBatch
{
    public Guid Id { get; set; }
    public string SourceSystem { get; set; } = "";
    public string Entity { get; set; } = "";
    public int RowsAccepted { get; set; }
    public int RowsRejected { get; set; }
    public string? RejectReasons { get; set; }
    public DateTime LoadedAt { get; set; }
    public string LoadedBy { get; set; } = "";
}

// ═══ J. Governance ═════════════════════════════════════════════════════════════════

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Break-glass local credential only (NFR-10); production signs in through OIDC.</summary>
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>Comma-separated hospital codes, or "*". Never taken from a request parameter.</summary>
    public string HospitalScope { get; set; } = "*";
    public string DeptScope { get; set; } = "*";
    public string Title { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ApprovalRequest
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string EntityLabel { get; set; } = "";
    public Guid? CostingVersionId { get; set; }
    public string Stage { get; set; } = "DRAFT";
    public Guid RequestedById { get; set; }
    public Guid? ReviewedById { get; set; }
    public string RequestNote { get; set; } = "";
    public string? ReviewNote { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string ActorEmail { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? EntityLabel { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Metadata { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset At { get; set; }
}
