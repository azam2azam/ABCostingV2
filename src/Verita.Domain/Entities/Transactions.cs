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
    /// <summary>HIS_Company.GroupID and its HIS_CompanyGroup: the insurer this company account belongs to.</summary>
    public int? HisGroupId { get; set; }
    public string? HisGroupName { get; set; }
    public bool? HisGroupIsTpa { get; set; }
    public bool? HisGroupManagedByTpa { get; set; }
    /// <summary>Set on import: from the HIS group, or from Name (see PayerParties) for a company with no group.</summary>
    public Guid? InsurerId { get; set; }
    public Guid? AdministratorId { get; set; }
}

/// <summary>The company that carries the risk, such as BUPA: a HIS_CompanyGroup. Several employer accounts (Payer) sit under one.</summary>
public class Insurer
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? HisGroupId { get; set; }
    public bool? IsTpa { get; set; }
    public bool? IsManagedByTpa { get; set; }
}

/// <summary>The claims administrator (TPA) handling an account for an insurer, such as TCS.</summary>
public class PayerAdministrator
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Reads the insurer and administrator out of a HIS account name: insurer[/administrator]_employer_number,
/// with the two companies in either order. A name without "_" is its own insurer, such as Self pay / cash.
/// </summary>
public static class PayerParties
{
    private static readonly Dictionary<string, string> Administrators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TCS"] = "TCS", ["GLOBEMED"] = "GLOBEMED", ["GLOBMED"] = "GLOBEMED",
        ["S.NEXTCARE"] = "S.NEXTCARE", ["S.N.CARE"] = "S.NEXTCARE", ["MEDNET"] = "MEDNET",
    };

    public static (string? Insurer, string? Administrator) Split(string name)
    {
        var whole = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var cut = whole.IndexOf('_');
        if (cut <= 0) return (whole.Length == 0 ? null : whole, null);
        var parts = whole[..cut].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();
        if (parts.Count == 0) return (whole, null);
        var administrator = parts.Select(x => Administrators.GetValueOrDefault(x)).FirstOrDefault(x => x is not null);
        var insurers = parts.Where(x => !Administrators.ContainsKey(x)).ToList();
        return (insurers.Count == 0 ? null : string.Join('/', insurers), administrator);
    }
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
    /// <summary>The HIS ER assessment this encounter came from, when it is merged under its appointment's number.</summary>
    public string? SourceAssessmentNo { get; set; }
    /// <summary>HIS states as stored: appointment, admission or ER assessment status, for reading against invoices.</summary>
    public int? HisStatus { get; set; }
    public int? HisErStatus { get; set; }
    public bool? HisVisitClosed { get; set; }
    public DateTime? HisArrivedAt { get; set; }
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
    /// <summary>A package component on a child invoice. Kept as HIS holds it, but the parent invoice line is what counts.</summary>
    public bool IsChildInvoice { get; set; }
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
    /// <summary>The HIS invoice line's own state, as stored. Not used by any calculation yet.</summary>
    public int? HisLineStatus { get; set; }
    public bool? IsPerformed { get; set; }
    public int? PerformType { get; set; }
    public bool? IsReturned { get; set; }
    public bool? IsRefunded { get; set; }
    public decimal? RefundedAmount { get; set; }
    /// <summary>The HIS line's money and package fields, as stored. Not used by any calculation.</summary>
    public decimal? HisUnitPrice { get; set; }
    public int? HisQuantity { get; set; }
    public string? DiscountType { get; set; }
    public decimal? HisDiscountAmount { get; set; }
    public decimal? WaivedAmount { get; set; }
    public int? WaiverId { get; set; }
    public decimal? PatientShare { get; set; }
    public decimal? CompanyShare { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? PatientTaxAmount { get; set; }
    public decimal? CompanyTaxAmount { get; set; }
    public decimal? OutlierPayment { get; set; }
    public decimal? ClaimedAmount { get; set; }
    public decimal? SettledAmount { get; set; }
    public decimal? RejectedAmount { get; set; }
    public bool? IsZeroPrice { get; set; }
    public bool? IsPackageSelected { get; set; }
    public string? PackageProcedureId { get; set; }
    public int? PackageSequenceNo { get; set; }
    public bool? IsPackageInvoice { get; set; }
    public int? BillType { get; set; }
    public bool? IsAcceptedWithDrg { get; set; }
    /// <summary>HIS_InvoiceMain.InvoiceType (2 = child) and MainInvoiceNo (its parent).</summary>
    public int? HisInvoiceType { get; set; }
    public string? HisMainInvoiceNo { get; set; }
    /// <summary>A package component on a child invoice. Kept as HIS holds it, but the parent invoice line is what counts.</summary>
    public bool IsChildInvoice { get; set; }
}

/// <summary>
/// One line of a HIS refund, as HIS holds it: cancelled and unapproved ones included, and not part of
/// revenue. EncounterId is empty when the refund's encounter is not loaded; HisEncounterNo still says which.
/// </summary>
public class RefundTransaction
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? EncounterId { get; set; }
    public string? HisEncounterNo { get; set; }
    public string? PatientMrn { get; set; }
    public Guid? ProcedureId { get; set; }
    public string? HisProcedureId { get; set; }
    public Guid? PayerId { get; set; }
    public string RefundNo { get; set; } = "";
    public int RefundType { get; set; }
    public DateTime RefundDate { get; set; }
    public int OriginalTransactionType { get; set; }
    public string OriginalInvoiceNo { get; set; } = "";
    public DateTime? OriginalInvoiceDate { get; set; }
    public int InvoiceLineNo { get; set; }
    public DateTime? InvoiceCancelledAt { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal PatientShare { get; set; }
    public decimal CompanyShare { get; set; }
    public decimal PatientPaid { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>The whole refund's amounts on HIS_RefundMain, repeated on each of its lines.</summary>
    public decimal HeaderRefundAmount { get; set; }
    public decimal HeaderAmountPaid { get; set; }
    public int Status { get; set; }
    public int? ApprovalStatus { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? ReasonId { get; set; }
    public string SourceSystem { get; set; } = "VIDA-HIS";
    public string SourceRef { get; set; } = "";
}

/// <summary>
/// One component of a package billed on a HIS invoice, as HIS holds it. Stored for reference: it is not
/// a service or revenue line, and no calculation reads it.
/// </summary>
public class InvoicePackageItem
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? EncounterId { get; set; }
    public string? HisEncounterNo { get; set; }
    public Guid? ProcedureId { get; set; }
    public string? HisProcedureId { get; set; }
    public string? PackageProcedureId { get; set; }
    public int? PackageLineItemNo { get; set; }
    public int? PackageSequence { get; set; }
    public int TransactionType { get; set; }
    public string InvoiceNo { get; set; } = "";
    public int LineItemNo { get; set; }
    public DateTime InvoiceDate { get; set; }
    public decimal Price { get; set; }
    public decimal DiscountedCharge { get; set; }
    public decimal PatientShare { get; set; }
    public decimal PatientPaid { get; set; }
    public decimal CompanyShare { get; set; }
    public decimal CompanyPaid { get; set; }
    public string? ChildInvoiceNo { get; set; }
    public int? ChildInvoiceLineItemNo { get; set; }
    public bool? IsCompleted { get; set; }
    public bool? IsRefunded { get; set; }
    public string SourceSystem { get; set; } = "VIDA-HIS";
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
