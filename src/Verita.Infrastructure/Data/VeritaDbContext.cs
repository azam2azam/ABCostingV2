using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Data;

/// <summary>
/// EF Core owns the schema, master data, configuration, governance and audit (ARCH-2). The
/// engine's hot path does not go through it: transactions are streamed with Dapper and results
/// written with SqlBulkCopy (ARCH-3, ARCH-4).
/// </summary>
public sealed class VeritaDbContext(DbContextOptions<VeritaDbContext> options) : DbContext(options)
{
    // Organisation
    public DbSet<Hospital> Hospitals => Set<Hospital>();
    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<ServiceLine> ServiceLines => Set<ServiceLine>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    // Financial
    public DbSet<GLAccount> GLAccounts => Set<GLAccount>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<GLPosting> GLPostings => Set<GLPosting>();
    public DbSet<CostPool> CostPools => Set<CostPool>();
    public DbSet<GLMapping> GLMappings => Set<GLMapping>();
    // Drivers
    public DbSet<CostDriver> CostDrivers => Set<CostDriver>();
    public DbSet<DriverVolume> DriverVolumes => Set<DriverVolume>();
    // Resources
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceRate> ResourceRates => Set<ResourceRate>();
    public DbSet<ResourceCapacity> ResourceCapacities => Set<ResourceCapacity>();
    public DbSet<Provider> Providers => Set<Provider>();
    // Catalogue
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ActivityResource> ActivityResources => Set<ActivityResource>();
    public DbSet<ActivityDriver> ActivityDrivers => Set<ActivityDriver>();
    public DbSet<Procedure> Procedures => Set<Procedure>();
    public DbSet<ProcedureRate> ProcedureRates => Set<ProcedureRate>();
    public DbSet<CostingDefinition> CostingDefinitions => Set<CostingDefinition>();
    public DbSet<ProcedureActivityLine> ProcedureActivityLines => Set<ProcedureActivityLine>();
    public DbSet<ProcedureResourceLine> ProcedureResourceLines => Set<ProcedureResourceLine>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageComponent> PackageComponents => Set<PackageComponent>();
    public DbSet<Drg> Drgs => Set<Drg>();
    // Clinical
    public DbSet<Payer> Payers => Set<Payer>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<EncounterService> EncounterServices => Set<EncounterService>();
    public DbSet<ActivityTransaction> ActivityTransactions => Set<ActivityTransaction>();
    public DbSet<RevenueTransaction> RevenueTransactions => Set<RevenueTransaction>();
    // Rules & versions
    public DbSet<AllocationRule> AllocationRules => Set<AllocationRule>();
    public DbSet<AllocationRuleTarget> AllocationRuleTargets => Set<AllocationRuleTarget>();
    public DbSet<CostingVersion> CostingVersions => Set<CostingVersion>();
    // Runs & results
    public DbSet<CalculationRun> CalculationRuns => Set<CalculationRun>();
    public DbSet<RunStep> RunSteps => Set<RunStep>();
    public DbSet<ResourceUnitCost> ResourceUnitCosts => Set<ResourceUnitCost>();
    public DbSet<CostPoolBalance> CostPoolBalances => Set<CostPoolBalance>();
    public DbSet<CostCenterBalance> CostCenterBalances => Set<CostCenterBalance>();
    public DbSet<AllocationResult> AllocationResults => Set<AllocationResult>();
    public DbSet<ActivityCost> ActivityCosts => Set<ActivityCost>();
    public DbSet<ProcedureCost> ProcedureCosts => Set<ProcedureCost>();
    public DbSet<EncounterCost> EncounterCosts => Set<EncounterCost>();
    public DbSet<PackageCost> PackageCosts => Set<PackageCost>();
    public DbSet<DrgCostSummary> DrgCostSummaries => Set<DrgCostSummary>();
    public DbSet<CostComponent> CostComponents => Set<CostComponent>();
    public DbSet<ResidualBucket> ResidualBuckets => Set<ResidualBucket>();
    // Analysis
    public DbSet<Variance> Variances => Set<Variance>();
    public DbSet<VarianceComponent> VarianceComponents => Set<VarianceComponent>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<ReconciliationLine> ReconciliationLines => Set<ReconciliationLine>();
    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<ScenarioLever> ScenarioLevers => Set<ScenarioLever>();
    public DbSet<ScenarioResult> ScenarioResults => Set<ScenarioResult>();
    public DbSet<DataQualityIssue> DataQualityIssues => Set<DataQualityIssue>();
    // Governance
    public DbSet<User> Users => Set<User>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    /// <summary>Master-data tables get SQL Server system versioning as an audit mechanism —
    /// never used to serve effective-dated costing (BRD section 18).</summary>
    public static readonly Type[] TemporalEntities =
    [
        typeof(Hospital), typeof(Facility), typeof(ServiceLine), typeof(Department), typeof(CostCenter), typeof(GLAccount),
        typeof(CostPool), typeof(GLMapping), typeof(CostDriver), typeof(Resource), typeof(ResourceRate), typeof(ResourceCapacity),
        typeof(Provider), typeof(Activity), typeof(ActivityResource), typeof(ActivityDriver), typeof(Procedure), typeof(ProcedureRate),
        typeof(CostingVersion), typeof(CostingDefinition), typeof(ProcedureActivityLine), typeof(ProcedureResourceLine),
        typeof(Package), typeof(PackageComponent), typeof(Drg), typeof(Payer), typeof(AllocationRule), typeof(AllocationRuleTarget), typeof(User),
    ];

    private static readonly HashSet<string> LongText =
    [
        "Description", "Explanation", "Narrative", "Summary", "Message", "DetailJson", "MetricsJson", "BeforeJson", "AfterJson",
        "Metadata", "RequestNote", "ReviewNote", "RejectReasons", "Notes", "ErrorMessage",
    ];

    private static readonly HashSet<string> JsonColumns = ["DetailJson", "MetricsJson", "BeforeJson", "AfterJson", "Metadata"];

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // CR-1: money is DECIMAL(19,4) by default, never FLOAT, REAL or MONEY.
        b.Properties<decimal>().HavePrecision(19, 4);
        b.Properties<string>().HaveMaxLength(200);
        b.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(3)");
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        foreach (var et in mb.Model.GetEntityTypes().ToList())
        {
            var clr = et.ClrType;
            var e = mb.Entity(clr);
            e.ToTable(clr.Name == "Drg" ? "DRG" : clr.Name, t =>
            {
                if (TemporalEntities.Contains(clr)) t.IsTemporal();
            });
            e.Property("Id").ValueGeneratedNever();

            foreach (var prop in clr.GetProperties())
            {
                if (prop.GetCustomAttribute<PreciseAttribute>() is not null) e.Property(prop.Name).HasPrecision(19, 6);
                if (prop.GetCustomAttribute<ShareAttribute>() is not null) e.Property(prop.Name).HasPrecision(9, 6);
                if (prop.PropertyType == typeof(string) && LongText.Contains(prop.Name)) e.Property(prop.Name).HasColumnType("nvarchar(max)");
                if (prop.PropertyType == typeof(DateTime) || prop.PropertyType == typeof(DateTime?)) e.Property(prop.Name).HasColumnType("datetime2(3)");
                if (prop.PropertyType == typeof(string) && prop.Name == "CalcExpression") e.Property(prop.Name).HasMaxLength(1000);
                if (prop.PropertyType == typeof(string) && JsonColumns.Contains(prop.Name))
                    e.ToTable(t => t.HasCheckConstraint($"CK_{clr.Name}_{prop.Name}_Json", $"[{prop.Name}] IS NULL OR ISJSON([{prop.Name}]) = 1"));
            }
        }

        // ── Enumerated columns: check constraints generated from the one declaration ──
        foreach (var (column, values) in Catalogue.ColumnDomains)
        {
            var parts = column.Split('.');
            var et = mb.Model.GetEntityTypes().First(t => t.ClrType.Name == parts[0]);
            var list = string.Join(", ", values.Select(v => $"N'{v}'"));
            mb.Entity(et.ClrType).ToTable(t => t.HasCheckConstraint($"CK_{parts[0]}_{parts[1]}", $"[{parts[1]}] IN ({list})"));
        }

        Keys(mb);
        Relationships(mb);
        Indexes(mb);

        // The traceability spine (BRD section 18): the arithmetic is checked at write time.
        mb.Entity<CostComponent>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_CostComponent_Amount", "[Amount] = ROUND([Quantity] * [UnitCost], 4)");
            t.HasCheckConstraint("CK_CostComponent_CalcExpression", "LEN([CalcExpression]) > 0");
        });
        mb.Entity<GLMapping>().ToTable(t => t.HasCheckConstraint("CK_GLMapping_Weight", "[Weight] > 0 AND [Weight] <= 1"));
        mb.Entity<AllocationRule>().ToTable(t => t.HasCheckConstraint("CK_AllocationRule_Stage", "[Stage] IN (1, 2, 3)"));
        foreach (var type in new[] { typeof(ResourceRate), typeof(ResourceCapacity), typeof(GLMapping), typeof(CostPool), typeof(AllocationRule), typeof(CostDriver), typeof(Activity), typeof(ProcedureRate), typeof(CostingVersion) })
            mb.Entity(type).ToTable(t => t.HasCheckConstraint($"CK_{type.Name}_EffectiveRange", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]"));
    }

    private static void Keys(ModelBuilder mb)
    {
        // Big append-only tables are clustered on their read pattern, not on the GUID (BRD section 18).
        mb.Entity<ActivityTransaction>().HasKey(x => x.Id).IsClustered(false);
        mb.Entity<ActivityTransaction>().HasIndex(x => new { x.EncounterId, x.Id }).IsClustered();
        mb.Entity<CostComponent>().HasKey(x => x.Id).IsClustered(false);
        mb.Entity<CostComponent>().HasIndex(x => new { x.RunId, x.Scope, x.ScopeId }).IsClustered();
        mb.Entity<EncounterCost>().HasKey(x => x.Id).IsClustered(false);
        mb.Entity<EncounterCost>().HasIndex(x => new { x.RunId, x.EncounterId }).IsUnique().IsClustered();
        mb.Entity<GLPosting>().HasKey(x => x.Id).IsClustered(false);
        // Clustered, so the index covers Amount, GLAccountId and CostCenterId without an INCLUDE.
        mb.Entity<GLPosting>().HasIndex(x => new { x.PeriodId, x.HospitalId }).IsClustered();
    }

    private static void Relationships(ModelBuilder mb)
    {
        void Fk<TDep, TPrin>(System.Linq.Expressions.Expression<Func<TDep, object?>> fk) where TDep : class where TPrin : class =>
            mb.Entity<TDep>().HasOne<TPrin>().WithMany().HasForeignKey(fk!).OnDelete(DeleteBehavior.NoAction);

        Fk<Facility, Hospital>(x => x.HospitalId);
        Fk<Department, Hospital>(x => x.HospitalId);
        Fk<Department, ServiceLine>(x => x.ServiceLineId);
        Fk<CostCenter, Hospital>(x => x.HospitalId);
        Fk<CostCenter, Department>(x => x.DepartmentId);
        Fk<CostCenter, Facility>(x => x.FacilityId);
        Fk<GLPosting, Hospital>(x => x.HospitalId);
        Fk<GLPosting, Period>(x => x.PeriodId);
        Fk<GLPosting, GLAccount>(x => x.GLAccountId);
        Fk<GLPosting, CostCenter>(x => x.CostCenterId);
        Fk<CostPool, Hospital>(x => x.HospitalId);
        Fk<CostPool, CostCenter>(x => x.CostCenterId);
        Fk<CostPool, CostDriver>(x => x.PrimaryDriverId);
        Fk<GLMapping, GLAccount>(x => x.GLAccountId);
        Fk<GLMapping, CostCenter>(x => x.CostCenterId);
        Fk<GLMapping, CostPool>(x => x.CostPoolId);
        Fk<CostDriver, Hospital>(x => x.HospitalId);
        Fk<CostDriver, CostCenter>(x => x.CostCenterId);
        Fk<CostDriver, CostPool>(x => x.CostPoolId);
        Fk<CostDriver, Activity>(x => x.ActivityId);
        Fk<DriverVolume, CostDriver>(x => x.DriverId);
        Fk<DriverVolume, Period>(x => x.PeriodId);
        Fk<DriverVolume, CostCenter>(x => x.CostCenterId);
        Fk<DriverVolume, Activity>(x => x.ActivityId);
        Fk<Resource, Hospital>(x => x.HospitalId);
        Fk<Resource, CostCenter>(x => x.CostCenterId);
        Fk<Resource, CostPool>(x => x.CostPoolId);
        Fk<ResourceRate, Resource>(x => x.ResourceId);
        Fk<ResourceCapacity, Resource>(x => x.ResourceId);
        Fk<Provider, Resource>(x => x.ResourceId);
        Fk<Provider, Department>(x => x.DepartmentId);
        Fk<Activity, Hospital>(x => x.HospitalId);
        Fk<Activity, Department>(x => x.DepartmentId);
        Fk<Activity, CostCenter>(x => x.CostCenterId);
        Fk<ActivityResource, Activity>(x => x.ActivityId);
        Fk<ActivityResource, Resource>(x => x.ResourceId);
        Fk<ActivityDriver, Activity>(x => x.ActivityId);
        Fk<ActivityDriver, CostDriver>(x => x.DriverId);
        Fk<Procedure, Hospital>(x => x.HospitalId);
        Fk<Procedure, Department>(x => x.DepartmentId);
        Fk<Procedure, ServiceLine>(x => x.ServiceLineId);
        Fk<ProcedureRate, Procedure>(x => x.ProcedureId);
        Fk<ProcedureRate, Hospital>(x => x.HospitalId);
        Fk<CostingDefinition, Procedure>(x => x.ProcedureId);
        Fk<CostingDefinition, CostingVersion>(x => x.CostingVersionId);
        Fk<ProcedureActivityLine, CostingDefinition>(x => x.DefinitionId);
        Fk<ProcedureActivityLine, Activity>(x => x.ActivityId);
        Fk<ProcedureResourceLine, CostingDefinition>(x => x.DefinitionId);
        Fk<ProcedureResourceLine, Resource>(x => x.ResourceId);
        Fk<Package, Hospital>(x => x.HospitalId);
        Fk<PackageComponent, Package>(x => x.PackageId);
        Fk<PackageComponent, Procedure>(x => x.ProcedureId);
        Fk<Patient, Hospital>(x => x.HospitalId);
        Fk<Encounter, Patient>(x => x.PatientId);
        Fk<Encounter, Hospital>(x => x.HospitalId);
        Fk<Encounter, Department>(x => x.DepartmentId);
        Fk<Encounter, ServiceLine>(x => x.ServiceLineId);
        Fk<Encounter, Payer>(x => x.PayerId);
        Fk<Encounter, Drg>(x => x.DrgId);
        Fk<Encounter, Package>(x => x.PackageId);
        Fk<Encounter, Provider>(x => x.ProviderId);
        Fk<EncounterService, Encounter>(x => x.EncounterId);
        Fk<EncounterService, Procedure>(x => x.ProcedureId);
        Fk<EncounterService, CostCenter>(x => x.CostCenterId);
        Fk<EncounterService, Provider>(x => x.ProviderId);
        Fk<ActivityTransaction, Encounter>(x => x.EncounterId);
        Fk<ActivityTransaction, EncounterService>(x => x.EncounterServiceId);
        Fk<ActivityTransaction, Activity>(x => x.ActivityId);
        Fk<ActivityTransaction, Resource>(x => x.ResourceId);
        Fk<ActivityTransaction, CostCenter>(x => x.CostCenterId);
        Fk<RevenueTransaction, Encounter>(x => x.EncounterId);
        Fk<RevenueTransaction, Procedure>(x => x.ProcedureId);
        Fk<RevenueTransaction, Payer>(x => x.PayerId);
        Fk<AllocationRule, CostPool>(x => x.SourcePoolId);
        Fk<AllocationRule, CostDriver>(x => x.DriverId);
        Fk<AllocationRuleTarget, AllocationRule>(x => x.RuleId);
        Fk<AllocationRuleTarget, CostCenter>(x => x.CostCenterId);
        Fk<CalculationRun, Hospital>(x => x.HospitalId);
        Fk<CalculationRun, Period>(x => x.PeriodId);
        Fk<CalculationRun, CostingVersion>(x => x.CostingVersionId);

        // Result tables belong to their run; deleting a (shadow) run removes them.
        void RunFk<T>() where T : class =>
            mb.Entity<T>().HasOne<CalculationRun>().WithMany().HasForeignKey("RunId").OnDelete(DeleteBehavior.Cascade);
        RunFk<RunStep>(); RunFk<ResourceUnitCost>(); RunFk<CostPoolBalance>(); RunFk<CostCenterBalance>(); RunFk<AllocationResult>();
        RunFk<ActivityCost>(); RunFk<ProcedureCost>(); RunFk<EncounterCost>(); RunFk<PackageCost>(); RunFk<DrgCostSummary>();
        RunFk<CostComponent>(); RunFk<ResidualBucket>(); RunFk<Variance>(); RunFk<Reconciliation>();
        mb.Entity<DataQualityIssue>().HasOne<CalculationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<VarianceComponent>().HasOne<Variance>().WithMany().HasForeignKey(x => x.VarianceId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ReconciliationLine>().HasOne<Reconciliation>().WithMany().HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ScenarioLever>().HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ScenarioResult>().HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Cascade);
        Fk<Scenario, CalculationRun>(x => x.BaseRunId);
        Fk<ApprovalRequest, User>(x => x.RequestedById);
        Fk<ApprovalRequest, User>(x => x.ReviewedById);
        Fk<ApprovalRequest, CostingVersion>(x => x.CostingVersionId);
        Fk<AuditLog, User>(x => x.UserId);
    }

    private static void Indexes(ModelBuilder mb)
    {
        void Unique<T>(System.Linq.Expressions.Expression<Func<T, object?>> cols) where T : class => mb.Entity<T>().HasIndex(cols!).IsUnique();

        Unique<Hospital>(x => x.Code); Unique<Facility>(x => x.Code); Unique<ServiceLine>(x => x.Code); Unique<Department>(x => x.Code);
        Unique<CostCenter>(x => x.Code); Unique<GLAccount>(x => x.Code); Unique<Period>(x => x.Code); Unique<CostPool>(x => x.Code);
        Unique<CostDriver>(x => x.Code); Unique<Resource>(x => x.Code); Unique<Provider>(x => x.Code); Unique<Activity>(x => x.Code);
        Unique<Procedure>(x => x.Code); Unique<Package>(x => x.Code); Unique<Drg>(x => x.Code); Unique<Payer>(x => x.Code);
        Unique<Patient>(x => x.Mrn); Unique<Encounter>(x => x.EncounterNo); Unique<AllocationRule>(x => x.Code);
        Unique<CostingVersion>(x => x.Code); Unique<CalculationRun>(x => x.Code); Unique<Scenario>(x => x.Code); Unique<User>(x => x.Email);
        Unique<ActivityResource>(x => new { x.ActivityId, x.ResourceId });
        Unique<ActivityDriver>(x => new { x.ActivityId, x.DriverId });
        Unique<CostingDefinition>(x => new { x.ProcedureId, x.CostingVersionId });
        Unique<ProcedureActivityLine>(x => new { x.DefinitionId, x.ActivityId });
        Unique<ProcedureResourceLine>(x => new { x.DefinitionId, x.ResourceId });
        Unique<PackageComponent>(x => new { x.PackageId, x.ProcedureId });

        // BRD section 18 constraints worth enforcing in the database.
        Unique<ResourceUnitCost>(x => new { x.RunId, x.ResourceId });
        Unique<CostPoolBalance>(x => new { x.RunId, x.CostPoolId });
        Unique<CostCenterBalance>(x => new { x.RunId, x.CostCenterId });
        Unique<ProcedureCost>(x => new { x.RunId, x.ProcedureId, x.CostType });
        Unique<ActivityCost>(x => new { x.RunId, x.ActivityId });
        Unique<PackageCost>(x => new { x.RunId, x.PackageId });
        Unique<DrgCostSummary>(x => new { x.RunId, x.DrgId });

        mb.Entity<ActivityTransaction>().HasIndex(x => x.ResourceId);
        mb.Entity<ActivityTransaction>().HasIndex(x => x.ActivityId);
        mb.Entity<ActivityTransaction>().HasIndex(x => x.OccurredAt);
        // CR-10: the natural key of a transaction includes its activity; load is idempotent on it.
        mb.Entity<ActivityTransaction>().HasIndex(x => new { x.SourceSystem, x.SourceRef });
        mb.Entity<CostComponent>().HasIndex(x => new { x.RunId, x.ComponentType });
        mb.Entity<CostComponent>().HasIndex(x => x.EncounterId);
        mb.Entity<CostComponent>().HasIndex(x => x.ProcedureId);
        mb.Entity<AllocationResult>().HasIndex(x => new { x.RunId, x.Stage });
        mb.Entity<AllocationResult>().HasIndex(x => x.TargetCostCenterId);
        mb.Entity<AllocationResult>().HasIndex(x => x.TargetActivityId);
        mb.Entity<Encounter>().HasIndex(x => new { x.HospitalId, x.AdmitAt });
        mb.Entity<EncounterService>().HasIndex(x => x.EncounterId);
        mb.Entity<RevenueTransaction>().HasIndex(x => x.EncounterId);
        mb.Entity<DriverVolume>().HasIndex(x => new { x.DriverId, x.PeriodId });
        mb.Entity<ResourceRate>().HasIndex(x => new { x.ResourceId, x.EffectiveFrom });
        mb.Entity<GLMapping>().HasIndex(x => new { x.GLAccountId, x.CostCenterId });
        mb.Entity<CalculationRun>().HasIndex(x => new { x.HospitalId, x.PeriodId });
        mb.Entity<CalculationRun>().HasIndex(x => x.Status);
        mb.Entity<Variance>().HasIndex(x => new { x.RunId, x.Scope });
        mb.Entity<DataQualityIssue>().HasIndex(x => new { x.RunId, x.Severity });
        mb.Entity<AuditLog>().HasIndex(x => x.At);
        mb.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });
        mb.Entity<AuditLog>().HasIndex(x => x.ActorEmail);
        mb.Entity<ApprovalRequest>().HasIndex(x => new { x.EntityType, x.EntityId });
    }
}
