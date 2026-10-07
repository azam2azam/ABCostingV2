using Verita.Domain.Entities;

namespace Verita.Infrastructure.DataHub;

/// <summary>A reference from a row to another table, written in files and forms by the target's code.</summary>
public sealed record RefSpec(string Column, string FkProperty, Type Target, string TargetKey, bool Optional = false);

/// <summary>
/// One loadable table. The template, the upload validator, the data-entry form and the list view
/// are all generated from this declaration (NFR-22): add a table here and it appears everywhere.
/// </summary>
public sealed record EntitySpec(
    string Name, Type Type, string Group, string Description, string[] Key, RefSpec[] Refs,
    /// <summary>Effective-dated rows whose change is a new version, never an edit (FR-1.15, FR-1.23).</summary>
    string[]? VersionGroup = null,
    string WritePermission = "masterdata.edit",
    string[]? Hidden = null,
    string[]? ExtraColumns = null)
{
    public bool Versioned => VersionGroup is not null;
    public bool IsTransaction => Group == "Monthly data";
}

public static class EntitySpecs
{
    private static RefSpec R(string col, string fk, Type t, string key = "Code", bool opt = false) => new(col, fk, t, key, opt);
    private static RefSpec Hosp => R("hospitalCode", "HospitalId", typeof(Hospital));

    /// <summary>In load order: every table comes after the tables it refers to.</summary>
    public static readonly EntitySpec[] All =
    [
        // ── Organisation ─────────────────────────────────────────────────────────
        new("Hospital", typeof(Hospital), "Organisation", "The reporting entity and primary access scope.", ["Code"], [], Hidden: ["CreatedAt"]),
        new("Facility", typeof(Facility), "Organisation", "A building; its floor area drives facility overhead.", ["Code"], [Hosp]),
        new("ServiceLine", typeof(ServiceLine), "Organisation", "Commercial roll-up shared by all hospitals.", ["Code"], []),
        new("Department", typeof(Department), "Organisation", "Clinical or support unit. catalogCode joins the same department across hospitals.", ["Code"],
            [Hosp, R("serviceLineCode", "ServiceLineId", typeof(ServiceLine), opt: true)]),
        new("CostCenter", typeof(CostCenter), "Organisation", "Financial unit where ledger money lands. Low stepDownSeq closes first.", ["Code"],
            [Hosp, R("departmentCode", "DepartmentId", typeof(Department)), R("facilityCode", "FacilityId", typeof(Facility), opt: true)]),

        // ── Financial ────────────────────────────────────────────────────────────
        new("GLAccount", typeof(GLAccount), "Financial", "Ledger account, shared by the group.", ["Code"], []),
        new("Period", typeof(Period), "Financial", "Accounting month, shared by the group.", ["Code"], []),
        new("CostDriver", typeof(CostDriver), "Financial", "Measurable cause of cost, with its source system and formula.", ["Code"],
            [Hosp, R("costCentreCode", "CostCenterId", typeof(CostCenter), opt: true)]),
        new("CostPool", typeof(CostPool), "Financial", "Homogeneous bundle of cost. Funded by a resource = consumption stream; otherwise allocated by rule.", ["Code"],
            [Hosp, R("costCentreCode", "CostCenterId", typeof(CostCenter)), R("primaryDriverCode", "PrimaryDriverId", typeof(CostDriver), opt: true)]),
        new("GLMapping", typeof(GLMapping), "Financial", "Maps an account at a cost centre to a pool. Weights per account and centre total 1.", ["GLAccountId", "CostCenterId", "CostPoolId", "EffectiveFrom"],
            [R("glAccountCode", "GLAccountId", typeof(GLAccount)), R("costCentreCode", "CostCenterId", typeof(CostCenter)), R("costPoolCode", "CostPoolId", typeof(CostPool))],
            VersionGroup: ["GLAccountId", "CostCenterId", "CostPoolId"]),

        // ── Resources ────────────────────────────────────────────────────────────
        new("Resource", typeof(Resource), "Resources", "Anything that costs money and is consumed: a role, an asset or a supply item.", ["Code"],
            [Hosp, R("costCentreCode", "CostCenterId", typeof(CostCenter)), R("costPoolCode", "CostPoolId", typeof(CostPool), opt: true)]),
        new("ResourceRate", typeof(ResourceRate), "Resources", "Effective-dated unit cost. A price change is a new version; the previous one is closed the day before.", ["ResourceId", "EffectiveFrom"],
            [R("resourceCode", "ResourceId", typeof(Resource))], VersionGroup: ["ResourceId"]),
        new("ResourceCapacity", typeof(ResourceCapacity), "Resources", "Practical capacity for labour, equipment and rooms. Rate = annual cost / practical minutes.", ["ResourceId", "EffectiveFrom"],
            [R("resourceCode", "ResourceId", typeof(Resource))], VersionGroup: ["ResourceId"]),
        new("Provider", typeof(Provider), "Resources", "A named physician pointing at the role resource that carries the rate.", ["Code"],
            [R("resourceCode", "ResourceId", typeof(Resource)), R("departmentCode", "DepartmentId", typeof(Department))]),

        // ── Service catalogue ────────────────────────────────────────────────────
        new("Activity", typeof(Activity), "Catalogue", "Unit of work, with standard, minimum and maximum duration in minutes.", ["Code"],
            [Hosp, R("departmentCode", "DepartmentId", typeof(Department)), R("costCentreCode", "CostCenterId", typeof(CostCenter))]),
        new("ActivityResource", typeof(ActivityResource), "Catalogue", "Standard recipe line: what one occurrence of an activity consumes.", ["ActivityId", "ResourceId"],
            [R("activityCode", "ActivityId", typeof(Activity)), R("resourceCode", "ResourceId", typeof(Resource))]),
        new("ActivityDriver", typeof(ActivityDriver), "Catalogue", "The driver an activity is measured by.", ["ActivityId", "DriverId"],
            [R("activityCode", "ActivityId", typeof(Activity)), R("driverCode", "DriverId", typeof(CostDriver))]),
        new("Procedure", typeof(Procedure), "Catalogue", "Billable service. catalogCode joins the same procedure across hospitals.", ["Code"],
            [Hosp, R("departmentCode", "DepartmentId", typeof(Department)), R("serviceLineCode", "ServiceLineId", typeof(ServiceLine), opt: true)]),
        new("ProcedureRate", typeof(ProcedureRate), "Catalogue", "Effective-dated list price.", ["ProcedureId", "HospitalId", "EffectiveFrom"],
            [R("procedureCode", "ProcedureId", typeof(Procedure)), Hosp], VersionGroup: ["ProcedureId", "HospitalId"]),
        new("CostingVersion", typeof(CostingVersion), "Catalogue", "Named set of recipes and rates. New versions start as DRAFT and are published through Governance.", ["Code"], [],
            Hidden: ["PublishedAt", "PublishedById", "CreatedAt"]),
        new("CostingDefinition", typeof(CostingDefinition), "Catalogue", "Binds a procedure to its recipe under one costing version.", ["ProcedureId", "CostingVersionId"],
            [R("procedureCode", "ProcedureId", typeof(Procedure)), R("costingVersionCode", "CostingVersionId", typeof(CostingVersion))]),
        new("ProcedureActivityLine", typeof(ProcedureActivityLine), "Catalogue", "Procedure recipe: an activity and how many times it occurs. The definition is created if missing.", ["DefinitionId", "ActivityId"],
            [R("activityCode", "ActivityId", typeof(Activity))], ExtraColumns: ["procedureCode", "costingVersionCode"]),
        new("ProcedureResourceLine", typeof(ProcedureResourceLine), "Catalogue", "Procedure recipe: a resource consumed directly, outside any activity.", ["DefinitionId", "ResourceId"],
            [R("resourceCode", "ResourceId", typeof(Resource))], ExtraColumns: ["procedureCode", "costingVersionCode"]),
        new("Package", typeof(Package), "Catalogue", "Bundle of procedures at a contracted price.", ["Code"], [Hosp]),
        new("PackageComponent", typeof(PackageComponent), "Catalogue", "A procedure inside a package.", ["PackageId", "ProcedureId"],
            [R("packageCode", "PackageId", typeof(Package)), R("procedureCode", "ProcedureId", typeof(Procedure))]),
        new("Drg", typeof(Drg), "Catalogue", "Diagnosis-related group, shared by the group.", ["Code"], []),
        new("Payer", typeof(Payer), "Catalogue", "Insurer or funder, with contract rate as a fraction of list price.", ["Code"], []),
        new("AllocationRule", typeof(AllocationRule), "Catalogue", "How a pool is distributed: stage 1 reciprocal, 2 step-down, 3 to activities.", ["Code"],
            [R("sourcePoolCode", "SourcePoolId", typeof(CostPool)), R("driverCode", "DriverId", typeof(CostDriver), opt: true)]),
        new("AllocationRuleTarget", typeof(AllocationRuleTarget), "Catalogue", "Agreed percentage for a PERCENTAGE rule.", ["RuleId", "CostCenterId"],
            [R("ruleCode", "RuleId", typeof(AllocationRule)), R("costCentreCode", "CostCenterId", typeof(CostCenter), opt: true)]),

        // ── Monthly data (BRD 22.1) ──────────────────────────────────────────────
        new("GLPosting", typeof(GLPosting), "Monthly data", "Trial-balance posting by account, cost centre and month (ERP, after close).", ["SourceRef"],
            [Hosp, R("periodCode", "PeriodId", typeof(Period)), R("glAccountCode", "GLAccountId", typeof(GLAccount)), R("costCentreCode", "CostCenterId", typeof(CostCenter))]),
        new("DriverVolume", typeof(DriverVolume), "Monthly data", "Measured driver quantity by cost centre and month.", ["DriverId", "PeriodId", "CostCenterId", "ActivityId"],
            [R("driverCode", "DriverId", typeof(CostDriver)), R("periodCode", "PeriodId", typeof(Period)), R("costCentreCode", "CostCenterId", typeof(CostCenter)),
             R("activityCode", "ActivityId", typeof(Activity), opt: true)]),
        new("Patient", typeof(Patient), "Monthly data", "Patient master from the HIS.", ["Mrn"], [Hosp]),
        new("Encounter", typeof(Encounter), "Monthly data", "Episode of care. Belongs to the month it was admitted in.", ["EncounterNo"],
            [R("patientMrn", "PatientId", typeof(Patient), "Mrn"), Hosp, R("departmentCode", "DepartmentId", typeof(Department)),
             R("serviceLineCode", "ServiceLineId", typeof(ServiceLine), opt: true), R("payerCode", "PayerId", typeof(Payer)),
             R("drgCode", "DrgId", typeof(Drg), opt: true), R("packageCode", "PackageId", typeof(Package), opt: true), R("providerCode", "ProviderId", typeof(Provider), opt: true)]),
        new("EncounterService", typeof(EncounterService), "Monthly data", "Service delivered in an encounter.", ["SourceRef"],
            [R("encounterNo", "EncounterId", typeof(Encounter), "EncounterNo"), R("procedureCode", "ProcedureId", typeof(Procedure)),
             R("costCentreCode", "CostCenterId", typeof(CostCenter)), R("providerCode", "ProviderId", typeof(Provider), opt: true)]),
        new("ActivityTransaction", typeof(ActivityTransaction), "Monthly data", "Measured consumption. sourceRef must include the activity (CR-10).", ["SourceSystem", "SourceRef"],
            [R("encounterNo", "EncounterId", typeof(Encounter), "EncounterNo"), R("serviceRef", "EncounterServiceId", typeof(EncounterService), "SourceRef", opt: true),
             R("activityCode", "ActivityId", typeof(Activity)), R("resourceCode", "ResourceId", typeof(Resource)), R("costCentreCode", "CostCenterId", typeof(CostCenter))]),
        new("RevenueTransaction", typeof(RevenueTransaction), "Monthly data", "Billed line from RCM.", ["SourceRef"],
            [R("encounterNo", "EncounterId", typeof(Encounter), "EncounterNo"), R("procedureCode", "ProcedureId", typeof(Procedure), opt: true), R("payerCode", "PayerId", typeof(Payer))]),

        // ── Access ───────────────────────────────────────────────────────────────
        new("User", typeof(User), "Access", "A person who signs in. hospitalScope is a comma-separated list of hospital codes, or *.", ["Email"], [],
            WritePermission: "admin.manage", Hidden: ["PasswordHash", "LastLoginAt", "CreatedAt"], ExtraColumns: ["password"]),
    ];

    public static EntitySpec? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
