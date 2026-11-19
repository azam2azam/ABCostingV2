-- VERITA: complete database schema for a NEW, EMPTY database. This file is the schema's definition: the API
-- never creates or changes tables, and there are no EF Core migrations. A schema change is made by editing this
-- file and writing an update script for existing databases (see README.md in this folder).
--
-- Run first, in the target database (after 00-drop-all.sql when starting over). It never creates or drops the
-- database itself, so it runs with database-level rights only (db_owner, or db_ddladmin):
--   SSMS: select the database, open this file, Execute; or
--   sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -i 01-schema.sql
-- Then run 02-seed-users.sql and 03-seed-hospitals.sql. Needs SQL Server 2016 or newer (temporal tables).
-- The schema matches the EF Core model at 2026-11-19 (HIS sync tables included).

-- Session settings SQL Server needs for filtered indexes, computed columns and schema-bound objects
-- (SSMS sets these already; sqlcmd does not).
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- Guard: refuse to run on a database that already has a VERITA schema (or any tables).
IF EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
BEGIN
    RAISERROR (N'This database already has tables. 01-schema.sql only runs on a new, empty database (run 00-drop-all.sql first to start over); nothing was changed.', 16, 1);
    SET NOEXEC ON;  -- SSMS keeps going after an error; this skips every following batch
END;
GO

BEGIN TRANSACTION;
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [CostingVersion] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [Status] nvarchar(200) NOT NULL,
    [PublishedAt] datetime2(3) NULL,
    [PublishedById] uniqueidentifier NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_CostingVersion] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CostingVersion_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_CostingVersion_Status] CHECK ([Status] IN (N''DRAFT'', N''REVIEW'', N''FINANCE_APPROVED'', N''CLINICAL_APPROVED'', N''PUBLISHED'', N''RETIRED'')),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[CostingVersionHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [DRG] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Mdc] nvarchar(200) NOT NULL,
    [RelativeWt] decimal(19,6) NOT NULL,
    [ExpectedLOS] decimal(19,6) NOT NULL,
    [BaseRate] decimal(19,4) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_DRG] PRIMARY KEY ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[DRGHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [GLAccount] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [NaturalClass] nvarchar(200) NOT NULL,
    [CostClass] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_GLAccount] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_GLAccount_CostClass] CHECK ([CostClass] IN (N''DIRECT'', N''INDIRECT'')),
    CONSTRAINT [CK_GLAccount_NaturalClass] CHECK ([NaturalClass] IN (N''SALARY'', N''BENEFITS'', N''DRUG'', N''CONSUMABLE'', N''IMPLANT'', N''DEPRECIATION'', N''MAINTENANCE'', N''UTILITY'', N''IT'', N''RENT'', N''OTHER'')),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[GLAccountHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Hospital] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [City] nvarchar(200) NOT NULL,
    [Currency] nvarchar(200) NOT NULL,
    [BedCount] int NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Hospital] PRIMARY KEY ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[HospitalHistory]))');
GO

CREATE TABLE [ImportBatch] (
    [Id] uniqueidentifier NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [Entity] nvarchar(200) NOT NULL,
    [RowsAccepted] int NOT NULL,
    [RowsRejected] int NOT NULL,
    [RejectReasons] nvarchar(max) NULL,
    [LoadedAt] datetime2(3) NOT NULL,
    [LoadedBy] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_ImportBatch] PRIMARY KEY ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Payer] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [PayerType] nvarchar(200) NOT NULL,
    [ContractRate] decimal(9,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Payer] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Payer_PayerType] CHECK ([PayerType] IN (N''GOVERNMENT'', N''INSURANCE'', N''CORPORATE'', N''SELF_PAY'')),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[PayerHistory]))');
GO

CREATE TABLE [Period] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [StartDate] date NOT NULL,
    [EndDate] date NOT NULL,
    [Days] int NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_Period] PRIMARY KEY ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ServiceLine] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Category] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ServiceLine] PRIMARY KEY ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ServiceLineHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [User] (
    [Id] uniqueidentifier NOT NULL,
    [Email] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [PasswordHash] nvarchar(200) NOT NULL,
    [Role] nvarchar(200) NOT NULL,
    [HospitalScope] nvarchar(200) NOT NULL,
    [DeptScope] nvarchar(200) NOT NULL,
    [Title] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [LastLoginAt] datetime2(3) NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_User] PRIMARY KEY ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[UserHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Facility] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [FacilityType] nvarchar(200) NOT NULL,
    [SquareMetres] decimal(19,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Facility] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Facility_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[FacilityHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Package] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [CatalogCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [Price] decimal(19,4) NOT NULL,
    [CoveredLOS] int NOT NULL,
    [IsActive] bit NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Package] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Package_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[PackageHistory]))');
GO

CREATE TABLE [Patient] (
    [Id] uniqueidentifier NOT NULL,
    [Mrn] nvarchar(200) NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [FullName] nvarchar(200) NOT NULL,
    [BirthYear] int NOT NULL,
    [Sex] nvarchar(200) NOT NULL,
    [Segment] nvarchar(200) NOT NULL,
    [NationalIdMasked] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_Patient] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Patient_Segment] CHECK ([Segment] IN (N'PEDIATRIC', N'ADULT', N'GERIATRIC', N'MATERNITY')),
    CONSTRAINT [FK_Patient_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE TABLE [CalculationRun] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [PeriodId] uniqueidentifier NOT NULL,
    [CostingVersionId] uniqueidentifier NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [RunType] nvarchar(200) NOT NULL,
    [StartedAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [DurationMs] int NULL,
    [MetricsJson] nvarchar(max) NULL,
    [Forced] bit NOT NULL,
    [TriggeredBy] nvarchar(200) NOT NULL,
    [ApprovedBy] nvarchar(200) NULL,
    [PublishedAt] datetime2(3) NULL,
    [ErrorMessage] nvarchar(max) NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_CalculationRun] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CalculationRun_MetricsJson_Json] CHECK ([MetricsJson] IS NULL OR ISJSON([MetricsJson]) = 1),
    CONSTRAINT [CK_CalculationRun_RunType] CHECK ([RunType] IN (N'STANDARD', N'ACTUAL', N'BUDGET', N'FORECAST', N'SIMULATION')),
    CONSTRAINT [CK_CalculationRun_Status] CHECK ([Status] IN (N'DRAFT', N'VALIDATING', N'VALIDATED', N'CALCULATING', N'CALCULATED', N'RECONCILED', N'REVIEWED', N'APPROVED', N'PUBLISHED', N'FAILED')),
    CONSTRAINT [FK_CalculationRun_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]),
    CONSTRAINT [FK_CalculationRun_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_CalculationRun_Period_PeriodId] FOREIGN KEY ([PeriodId]) REFERENCES [Period] ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Department] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [ServiceLineId] uniqueidentifier NULL,
    [Code] nvarchar(200) NOT NULL,
    [CatalogCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [DeptType] nvarchar(200) NOT NULL,
    [Specialty] nvarchar(200) NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Department] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Department_DeptType] CHECK ([DeptType] IN (N''CLINICAL'', N''ANCILLARY'', N''SUPPORT'', N''ADMIN'')),
    CONSTRAINT [FK_Department_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_Department_ServiceLine_ServiceLineId] FOREIGN KEY ([ServiceLineId]) REFERENCES [ServiceLine] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[DepartmentHistory]))');
GO

CREATE TABLE [ApprovalRequest] (
    [Id] uniqueidentifier NOT NULL,
    [EntityType] nvarchar(200) NOT NULL,
    [EntityId] uniqueidentifier NOT NULL,
    [EntityLabel] nvarchar(200) NOT NULL,
    [CostingVersionId] uniqueidentifier NULL,
    [Stage] nvarchar(200) NOT NULL,
    [RequestedById] uniqueidentifier NOT NULL,
    [ReviewedById] uniqueidentifier NULL,
    [RequestNote] nvarchar(max) NOT NULL,
    [ReviewNote] nvarchar(max) NULL,
    [RequestedAt] datetime2(3) NOT NULL,
    [ReviewedAt] datetime2(3) NULL,
    CONSTRAINT [PK_ApprovalRequest] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ApprovalRequest_Stage] CHECK ([Stage] IN (N'DRAFT', N'REVIEW', N'FINANCE_APPROVAL', N'CLINICAL_APPROVAL', N'PUBLISHED', N'REJECTED', N'RETIRED')),
    CONSTRAINT [FK_ApprovalRequest_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]),
    CONSTRAINT [FK_ApprovalRequest_User_RequestedById] FOREIGN KEY ([RequestedById]) REFERENCES [User] ([Id]),
    CONSTRAINT [FK_ApprovalRequest_User_ReviewedById] FOREIGN KEY ([ReviewedById]) REFERENCES [User] ([Id])
);
GO

CREATE TABLE [AuditLog] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NULL,
    [ActorEmail] nvarchar(200) NOT NULL,
    [Action] nvarchar(200) NOT NULL,
    [EntityType] nvarchar(200) NOT NULL,
    [EntityId] nvarchar(200) NULL,
    [EntityLabel] nvarchar(200) NULL,
    [BeforeJson] nvarchar(max) NULL,
    [AfterJson] nvarchar(max) NULL,
    [Metadata] nvarchar(max) NULL,
    [IpAddress] nvarchar(200) NULL,
    [At] datetimeoffset(3) NOT NULL,
    CONSTRAINT [PK_AuditLog] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AuditLog_Action] CHECK ([Action] IN (N'CREATE', N'UPDATE', N'DELETE', N'APPROVE', N'REJECT', N'PUBLISH', N'RUN', N'LOGIN', N'LOGOUT', N'VIEW_PHI', N'EXPORT')),
    CONSTRAINT [CK_AuditLog_AfterJson_Json] CHECK ([AfterJson] IS NULL OR ISJSON([AfterJson]) = 1),
    CONSTRAINT [CK_AuditLog_BeforeJson_Json] CHECK ([BeforeJson] IS NULL OR ISJSON([BeforeJson]) = 1),
    CONSTRAINT [CK_AuditLog_Metadata_Json] CHECK ([Metadata] IS NULL OR ISJSON([Metadata]) = 1),
    CONSTRAINT [FK_AuditLog_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [User] ([Id])
);
GO

CREATE TABLE [ActivityCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [ActivityId] uniqueidentifier NOT NULL,
    [Volume] decimal(19,6) NOT NULL,
    [DirectCost] decimal(19,4) NOT NULL,
    [IndirectCost] decimal(19,4) NOT NULL,
    [OverheadCost] decimal(19,4) NOT NULL,
    [TotalCost] decimal(19,4) NOT NULL,
    [UnitCost] decimal(19,6) NOT NULL,
    [CalcExpression] nvarchar(1000) NOT NULL,
    CONSTRAINT [PK_ActivityCost] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ActivityCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [AllocationResult] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [RuleId] uniqueidentifier NULL,
    [SourcePoolId] uniqueidentifier NULL,
    [SourceCostCenterId] uniqueidentifier NULL,
    [TargetType] nvarchar(200) NOT NULL,
    [TargetCostCenterId] uniqueidentifier NULL,
    [TargetActivityId] uniqueidentifier NULL,
    [TargetLabel] nvarchar(200) NOT NULL,
    [Stage] int NOT NULL,
    [Iteration] int NOT NULL,
    [DriverId] uniqueidentifier NULL,
    [DriverQty] decimal(19,6) NOT NULL,
    [DriverVolume] decimal(19,6) NOT NULL,
    [DriverShare] decimal(19,6) NOT NULL,
    [Amount] decimal(19,4) NOT NULL,
    [CalcExpression] nvarchar(1000) NOT NULL,
    CONSTRAINT [PK_AllocationResult] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_AllocationResult_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [CostCenterBalance] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [DirectAmount] decimal(19,4) NOT NULL,
    [ReceivedAmount] decimal(19,4) NOT NULL,
    [AllocatedOut] decimal(19,4) NOT NULL,
    [FinalAmount] decimal(19,4) NOT NULL,
    [StepDownSeq] int NOT NULL,
    [IsClosed] bit NOT NULL,
    CONSTRAINT [PK_CostCenterBalance] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CostCenterBalance_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [CostComponent] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [CostingVersionId] uniqueidentifier NULL,
    [Scope] nvarchar(200) NOT NULL,
    [ScopeId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [ProcedureId] uniqueidentifier NULL,
    [ActivityId] uniqueidentifier NULL,
    [ResourceId] uniqueidentifier NULL,
    [CostPoolId] uniqueidentifier NULL,
    [DriverId] uniqueidentifier NULL,
    [AllocationRuleId] uniqueidentifier NULL,
    [ComponentType] nvarchar(200) NOT NULL,
    [CostNature] nvarchar(200) NOT NULL,
    [Label] nvarchar(200) NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [UnitCost] decimal(19,6) NOT NULL,
    [Amount] decimal(19,4) NOT NULL,
    [CalcExpression] nvarchar(1000) NOT NULL,
    [SourceSystem] nvarchar(200) NULL,
    [SourceRef] nvarchar(200) NULL,
    [CalculatedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_CostComponent] PRIMARY KEY NONCLUSTERED ([Id]),
    CONSTRAINT [CK_CostComponent_Amount] CHECK ([Amount] = ROUND([Quantity] * [UnitCost], 4)),
    CONSTRAINT [CK_CostComponent_CalcExpression] CHECK (LEN([CalcExpression]) > 0),
    CONSTRAINT [CK_CostComponent_ComponentType] CHECK ([ComponentType] IN (N'LABOR_PHYSICIAN', N'LABOR_NURSE', N'LABOR_TECH', N'LABOR_ALLIED', N'DRUG', N'CONSUMABLE', N'IMPLANT', N'BLOOD', N'EQUIPMENT', N'ROOM', N'UTILITY', N'IT_SERVICE', N'FACILITY', N'OVERHEAD')),
    CONSTRAINT [CK_CostComponent_CostNature] CHECK ([CostNature] IN (N'DIRECT', N'INDIRECT', N'OVERHEAD')),
    CONSTRAINT [FK_CostComponent_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [CostPoolBalance] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [CostPoolId] uniqueidentifier NOT NULL,
    [DirectAmount] decimal(19,4) NOT NULL,
    [ReceivedAmount] decimal(19,4) NOT NULL,
    [AllocatedOut] decimal(19,4) NOT NULL,
    [Residual] decimal(19,4) NOT NULL,
    [IsClosed] bit NOT NULL,
    CONSTRAINT [PK_CostPoolBalance] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CostPoolBalance_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [DataQualityIssue] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NULL,
    [CheckCode] nvarchar(200) NOT NULL,
    [CheckName] nvarchar(200) NOT NULL,
    [Category] nvarchar(200) NOT NULL,
    [Severity] nvarchar(200) NOT NULL,
    [EntityType] nvarchar(200) NOT NULL,
    [EntityId] nvarchar(200) NULL,
    [EntityLabel] nvarchar(200) NOT NULL,
    [Message] nvarchar(max) NOT NULL,
    [Value] nvarchar(200) NULL,
    [DetectedAt] datetime2(3) NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_DataQualityIssue] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_DataQualityIssue_Severity] CHECK ([Severity] IN (N'BLOCKING', N'ERROR', N'WARNING', N'INFO')),
    CONSTRAINT [FK_DataQualityIssue_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [DrgCostSummary] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [DrgId] uniqueidentifier NOT NULL,
    [CaseCount] int NOT NULL,
    [AvgCost] decimal(19,4) NOT NULL,
    [MedianCost] decimal(19,4) NOT NULL,
    [P25Cost] decimal(19,4) NOT NULL,
    [P75Cost] decimal(19,4) NOT NULL,
    [P90Cost] decimal(19,4) NOT NULL,
    [MinCost] decimal(19,4) NOT NULL,
    [MaxCost] decimal(19,4) NOT NULL,
    [StdDevCost] decimal(19,4) NOT NULL,
    [AvgLOS] decimal(19,6) NOT NULL,
    [CostPerDay] decimal(19,4) NOT NULL,
    [AvgReimbursement] decimal(19,4) NOT NULL,
    [AvgMargin] decimal(19,4) NOT NULL,
    [MarginPct] decimal(19,6) NOT NULL,
    [VariationCoeff] decimal(19,6) NOT NULL,
    [OutlierCount] int NOT NULL,
    CONSTRAINT [PK_DrgCostSummary] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_DrgCostSummary_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [EncounterCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [DirectCost] decimal(19,4) NOT NULL,
    [IndirectCost] decimal(19,4) NOT NULL,
    [OverheadCost] decimal(19,4) NOT NULL,
    [TotalCost] decimal(19,4) NOT NULL,
    [StandardCost] decimal(19,4) NOT NULL,
    [Revenue] decimal(19,4) NOT NULL,
    [ContributionMargin] decimal(19,4) NOT NULL,
    [OperatingMargin] decimal(19,4) NOT NULL,
    [MarginPct] decimal(19,6) NOT NULL,
    [CostPerDay] decimal(19,4) NOT NULL,
    [ServiceCount] int NOT NULL,
    [WeightedActivityUnits] decimal(19,6) NOT NULL,
    CONSTRAINT [PK_EncounterCost] PRIMARY KEY NONCLUSTERED ([Id]),
    CONSTRAINT [FK_EncounterCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [PackageCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [PackageId] uniqueidentifier NOT NULL,
    [CaseCount] int NOT NULL,
    [StandardCost] decimal(19,4) NOT NULL,
    [ActualCost] decimal(19,4) NOT NULL,
    [Revenue] decimal(19,4) NOT NULL,
    [Variance] decimal(19,4) NOT NULL,
    [VariancePct] decimal(19,6) NOT NULL,
    [ContributionMargin] decimal(19,4) NOT NULL,
    [OperatingMargin] decimal(19,4) NOT NULL,
    [MarginPct] decimal(19,6) NOT NULL,
    CONSTRAINT [PK_PackageCost] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PackageCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [ProcedureCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [CostType] nvarchar(200) NOT NULL,
    [Volume] decimal(19,6) NOT NULL,
    [DirectCost] decimal(19,4) NOT NULL,
    [IndirectCost] decimal(19,4) NOT NULL,
    [OverheadCost] decimal(19,4) NOT NULL,
    [TotalCost] decimal(19,4) NOT NULL,
    [UnitCost] decimal(19,6) NOT NULL,
    [Revenue] decimal(19,4) NOT NULL,
    [ContributionMargin] decimal(19,4) NOT NULL,
    [OperatingMargin] decimal(19,4) NOT NULL,
    CONSTRAINT [PK_ProcedureCost] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ProcedureCost_CostType] CHECK ([CostType] IN (N'STANDARD', N'ACTUAL', N'BUDGET', N'FORECAST', N'TARGET')),
    CONSTRAINT [FK_ProcedureCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [Reconciliation] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [GlTotal] decimal(19,4) NOT NULL,
    [PatientCost] decimal(19,4) NOT NULL,
    [ResidualTotal] decimal(19,4) NOT NULL,
    [Unexplained] decimal(19,4) NOT NULL,
    [Materiality] decimal(19,4) NOT NULL,
    [AttributionPct] decimal(19,6) NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [Narrative] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Reconciliation] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Reconciliation_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [ResidualBucket] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [Bucket] nvarchar(200) NOT NULL,
    [Label] nvarchar(200) NOT NULL,
    [Amount] decimal(19,4) NOT NULL,
    [DetailJson] nvarchar(max) NULL,
    [Explanation] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_ResidualBucket] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ResidualBucket_Bucket] CHECK ([Bucket] IN (N'UNUSED_CAPACITY', N'OVER_CAPACITY', N'NON_PATIENT_ACTIVITY', N'UNMAPPED_GL', N'NO_DRIVER_VOLUME', N'ROUNDING')),
    CONSTRAINT [CK_ResidualBucket_DetailJson_Json] CHECK ([DetailJson] IS NULL OR ISJSON([DetailJson]) = 1),
    CONSTRAINT [FK_ResidualBucket_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [ResourceUnitCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [AnnualCost] decimal(19,4) NOT NULL,
    [PeriodCost] decimal(19,4) NOT NULL,
    [TheoreticalMinutes] decimal(19,6) NOT NULL,
    [PracticalMinutes] decimal(19,6) NOT NULL,
    [AvailableMinutes] decimal(19,6) NOT NULL,
    [UsedMinutes] decimal(19,6) NOT NULL,
    [UnusedMinutes] decimal(19,6) NOT NULL,
    [UnitCost] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [CostOfUsedCapacity] decimal(19,4) NOT NULL,
    [CostOfUnusedCapacity] decimal(19,4) NOT NULL,
    [UtilisationPct] decimal(19,6) NOT NULL,
    [CalcExpression] nvarchar(1000) NOT NULL,
    CONSTRAINT [PK_ResourceUnitCost] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ResourceUnitCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [RunStep] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [StepNo] int NOT NULL,
    [StepCode] nvarchar(200) NOT NULL,
    [StepName] nvarchar(200) NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [StartedAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [DurationMs] int NULL,
    [RowsWritten] int NOT NULL,
    [MoneyIn] decimal(19,4) NOT NULL,
    [MoneyOut] decimal(19,4) NOT NULL,
    [Message] nvarchar(max) NULL,
    [MetricsJson] nvarchar(max) NULL,
    CONSTRAINT [PK_RunStep] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_RunStep_MetricsJson_Json] CHECK ([MetricsJson] IS NULL OR ISJSON([MetricsJson]) = 1),
    CONSTRAINT [CK_RunStep_Status] CHECK ([Status] IN (N'PENDING', N'RUNNING', N'SUCCESS', N'WARNING', N'FAILED')),
    CONSTRAINT [FK_RunStep_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [Scenario] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [BaseRunId] uniqueidentifier NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [CreatedBy] nvarchar(200) NOT NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [ExecutedAt] datetime2(3) NULL,
    CONSTRAINT [PK_Scenario] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Scenario_Status] CHECK ([Status] IN (N'DRAFT', N'RUNNING', N'COMPLETED', N'FAILED', N'ARCHIVED')),
    CONSTRAINT [FK_Scenario_CalculationRun_BaseRunId] FOREIGN KEY ([BaseRunId]) REFERENCES [CalculationRun] ([Id])
);
GO

CREATE TABLE [Variance] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [Scope] nvarchar(200) NOT NULL,
    [ScopeId] uniqueidentifier NOT NULL,
    [ScopeLabel] nvarchar(200) NOT NULL,
    [Baseline] nvarchar(200) NOT NULL,
    [ActualAmount] decimal(19,4) NOT NULL,
    [BaselineAmount] decimal(19,4) NOT NULL,
    [VarianceAmount] decimal(19,4) NOT NULL,
    [VariancePct] decimal(19,6) NOT NULL,
    [Direction] nvarchar(200) NOT NULL,
    [CaseCount] int NOT NULL,
    [Summary] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Variance] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Variance_Baseline] CHECK ([Baseline] IN (N'STANDARD', N'BUDGET', N'FORECAST', N'TARGET', N'PRIOR_PERIOD')),
    CONSTRAINT [CK_Variance_Direction] CHECK ([Direction] IN (N'FAVOURABLE', N'UNFAVOURABLE', N'NEUTRAL')),
    CONSTRAINT [FK_Variance_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [CostCenter] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [FacilityId] uniqueidentifier NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [CcType] nvarchar(200) NOT NULL,
    [StepDownSeq] int NOT NULL,
    [IsActive] bit NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_CostCenter] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CostCenter_CcType] CHECK ([CcType] IN (N''PRODUCTION'', N''ANCILLARY'', N''SUPPORT'', N''OVERHEAD'')),
    CONSTRAINT [FK_CostCenter_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_CostCenter_Facility_FacilityId] FOREIGN KEY ([FacilityId]) REFERENCES [Facility] ([Id]),
    CONSTRAINT [FK_CostCenter_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[CostCenterHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Procedure] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [CatalogCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Category] nvarchar(200) NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [ServiceLineId] uniqueidentifier NULL,
    [ProcType] nvarchar(200) NOT NULL,
    [Complexity] int NOT NULL,
    [IsActive] bit NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Procedure] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Procedure_ProcType] CHECK ([ProcType] IN (N''PROCEDURE'', N''SURGERY'', N''CONSULT'', N''LAB'', N''IMAGING'', N''BED_DAY'', N''ER_VISIT'', N''THERAPY'')),
    CONSTRAINT [FK_Procedure_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_Procedure_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_Procedure_ServiceLine_ServiceLineId] FOREIGN KEY ([ServiceLineId]) REFERENCES [ServiceLine] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ProcedureHistory]))');
GO

CREATE TABLE [ReconciliationLine] (
    [Id] uniqueidentifier NOT NULL,
    [ReconciliationId] uniqueidentifier NOT NULL,
    [Sequence] int NOT NULL,
    [Label] nvarchar(200) NOT NULL,
    [LineType] nvarchar(200) NOT NULL,
    [Bucket] nvarchar(200) NULL,
    [Amount] decimal(19,4) NOT NULL,
    [RunningBalance] decimal(19,4) NOT NULL,
    [Explanation] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_ReconciliationLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ReconciliationLine_Reconciliation_ReconciliationId] FOREIGN KEY ([ReconciliationId]) REFERENCES [Reconciliation] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [ScenarioLever] (
    [Id] uniqueidentifier NOT NULL,
    [ScenarioId] uniqueidentifier NOT NULL,
    [LeverType] nvarchar(200) NOT NULL,
    [TargetType] nvarchar(200) NOT NULL,
    [TargetId] nvarchar(200) NULL,
    [TargetLabel] nvarchar(200) NOT NULL,
    [ChangeType] nvarchar(200) NOT NULL,
    [Value] decimal(19,6) NOT NULL,
    [Note] nvarchar(200) NULL,
    CONSTRAINT [PK_ScenarioLever] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ScenarioLever_ChangeType] CHECK ([ChangeType] IN (N'PCT', N'ABS', N'SET')),
    CONSTRAINT [CK_ScenarioLever_LeverType] CHECK ([LeverType] IN (N'RESOURCE_RATE', N'RESOURCE_CAPACITY', N'ACTIVITY_DURATION', N'DRIVER_VOLUME', N'PROCEDURE_VOLUME', N'PATIENT_VOLUME', N'LOS', N'OCCUPANCY', N'REIMBURSEMENT', N'OVERHEAD_RATE', N'DRUG_PRICE', N'CONSUMABLE_PRICE', N'EQUIPMENT_COST', N'STAFF_UTILISATION')),
    CONSTRAINT [FK_ScenarioLever_Scenario_ScenarioId] FOREIGN KEY ([ScenarioId]) REFERENCES [Scenario] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [ScenarioResult] (
    [Id] uniqueidentifier NOT NULL,
    [ScenarioId] uniqueidentifier NOT NULL,
    [Dimension] nvarchar(200) NOT NULL,
    [DimensionId] nvarchar(200) NULL,
    [Label] nvarchar(200) NOT NULL,
    [BaselineCost] decimal(19,4) NOT NULL,
    [ScenarioCost] decimal(19,4) NOT NULL,
    [CostDelta] decimal(19,4) NOT NULL,
    [CostDeltaPct] decimal(19,6) NOT NULL,
    [BaselineRevenue] decimal(19,4) NOT NULL,
    [ScenarioRevenue] decimal(19,4) NOT NULL,
    [RevenueDelta] decimal(19,4) NOT NULL,
    [BaselineMargin] decimal(19,4) NOT NULL,
    [ScenarioMargin] decimal(19,4) NOT NULL,
    [MarginDelta] decimal(19,4) NOT NULL,
    [Explanation] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_ScenarioResult] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ScenarioResult_Scenario_ScenarioId] FOREIGN KEY ([ScenarioId]) REFERENCES [Scenario] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [VarianceComponent] (
    [Id] uniqueidentifier NOT NULL,
    [VarianceId] uniqueidentifier NOT NULL,
    [ComponentType] nvarchar(200) NOT NULL,
    [Label] nvarchar(200) NOT NULL,
    [ActualQty] decimal(19,6) NOT NULL,
    [StandardQty] decimal(19,6) NOT NULL,
    [ActualRate] decimal(19,6) NOT NULL,
    [StandardRate] decimal(19,6) NOT NULL,
    [RateEffect] decimal(19,4) NOT NULL,
    [QuantityEffect] decimal(19,4) NOT NULL,
    [MixEffect] decimal(19,4) NOT NULL,
    [TotalEffect] decimal(19,4) NOT NULL,
    [Explanation] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_VarianceComponent] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_VarianceComponent_Variance_VarianceId] FOREIGN KEY ([VarianceId]) REFERENCES [Variance] ([Id]) ON DELETE CASCADE
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Activity] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [CatalogCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Category] nvarchar(200) NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Specialty] nvarchar(200) NULL,
    [StdDuration] decimal(19,6) NOT NULL,
    [MinDuration] decimal(19,6) NOT NULL,
    [MaxDuration] decimal(19,6) NOT NULL,
    [Variability] nvarchar(200) NOT NULL,
    [Directness] nvarchar(200) NOT NULL,
    [Version] int NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Activity] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Activity_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_Activity_Variability] CHECK ([Variability] IN (N''FIXED'', N''VARIABLE'', N''SEMI_VARIABLE'')),
    CONSTRAINT [FK_Activity_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_Activity_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_Activity_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ActivityHistory]))');
GO

CREATE TABLE [GLPosting] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [PeriodId] uniqueidentifier NOT NULL,
    [GLAccountId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Amount] decimal(19,4) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(200) NOT NULL,
    [PostedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_GLPosting] PRIMARY KEY NONCLUSTERED ([Id]),
    CONSTRAINT [FK_GLPosting_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_GLPosting_GLAccount_GLAccountId] FOREIGN KEY ([GLAccountId]) REFERENCES [GLAccount] ([Id]),
    CONSTRAINT [FK_GLPosting_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_GLPosting_Period_PeriodId] FOREIGN KEY ([PeriodId]) REFERENCES [Period] ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [CostingDefinition] (
    [Id] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [CostingVersionId] uniqueidentifier NOT NULL,
    [Notes] nvarchar(max) NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_CostingDefinition] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CostingDefinition_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]),
    CONSTRAINT [FK_CostingDefinition_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[CostingDefinitionHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [PackageComponent] (
    [Id] uniqueidentifier NOT NULL,
    [PackageId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_PackageComponent] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PackageComponent_Package_PackageId] FOREIGN KEY ([PackageId]) REFERENCES [Package] ([Id]),
    CONSTRAINT [FK_PackageComponent_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[PackageComponentHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ProcedureRate] (
    [Id] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [ListPrice] decimal(19,4) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ProcedureRate] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ProcedureRate_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [FK_ProcedureRate_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_ProcedureRate_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ProcedureRateHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ProcedureActivityLine] (
    [Id] uniqueidentifier NOT NULL,
    [DefinitionId] uniqueidentifier NOT NULL,
    [ActivityId] uniqueidentifier NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ProcedureActivityLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ProcedureActivityLine_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    CONSTRAINT [FK_ProcedureActivityLine_CostingDefinition_DefinitionId] FOREIGN KEY ([DefinitionId]) REFERENCES [CostingDefinition] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ProcedureActivityLineHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ActivityDriver] (
    [Id] uniqueidentifier NOT NULL,
    [ActivityId] uniqueidentifier NOT NULL,
    [DriverId] uniqueidentifier NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [Weight] decimal(9,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ActivityDriver] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ActivityDriver_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ActivityDriverHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ActivityResource] (
    [Id] uniqueidentifier NOT NULL,
    [ActivityId] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [StdQuantity] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ActivityResource] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ActivityResource_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ActivityResourceHistory]))');
GO

CREATE TABLE [ActivityTransaction] (
    [Id] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [EncounterServiceId] uniqueidentifier NULL,
    [ActivityId] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [OccurredAt] datetime2(3) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_ActivityTransaction] PRIMARY KEY NONCLUSTERED ([Id]),
    CONSTRAINT [FK_ActivityTransaction_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    CONSTRAINT [FK_ActivityTransaction_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [AllocationRule] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [SourcePoolId] uniqueidentifier NOT NULL,
    [DriverId] uniqueidentifier NULL,
    [Method] nvarchar(200) NOT NULL,
    [Methodology] nvarchar(200) NOT NULL,
    [Stage] int NOT NULL,
    [Sequence] int NOT NULL,
    [PoolShare] decimal(9,6) NOT NULL,
    [Formula] nvarchar(200) NULL,
    [Status] nvarchar(200) NOT NULL,
    [Version] int NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_AllocationRule] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AllocationRule_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_AllocationRule_Method] CHECK ([Method] IN (N''PERCENTAGE'', N''VOLUME'', N''TIME'', N''QUANTITY'', N''VALUE'', N''WEIGHTED'', N''FORMULA'')),
    CONSTRAINT [CK_AllocationRule_Methodology] CHECK ([Methodology] IN (N''DIRECT'', N''STEP_DOWN'', N''RECIPROCAL'', N''ABC'', N''TDABC'')),
    CONSTRAINT [CK_AllocationRule_Stage] CHECK ([Stage] IN (1, 2, 3)),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[AllocationRuleHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [AllocationRuleTarget] (
    [Id] uniqueidentifier NOT NULL,
    [RuleId] uniqueidentifier NOT NULL,
    [TargetType] nvarchar(200) NOT NULL,
    [CostCenterId] uniqueidentifier NULL,
    [Percentage] decimal(9,6) NULL,
    [Weight] decimal(9,6) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_AllocationRuleTarget] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_AllocationRuleTarget_AllocationRule_RuleId] FOREIGN KEY ([RuleId]) REFERENCES [AllocationRule] ([Id]),
    CONSTRAINT [FK_AllocationRuleTarget_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[AllocationRuleTargetHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [CostDriver] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [CatalogCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [Unit] nvarchar(200) NOT NULL,
    [DriverType] nvarchar(200) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceField] nvarchar(200) NOT NULL,
    [Formula] nvarchar(200) NOT NULL,
    [Frequency] nvarchar(200) NOT NULL,
    [CostCenterId] uniqueidentifier NULL,
    [CostPoolId] uniqueidentifier NULL,
    [ActivityId] uniqueidentifier NULL,
    [AllocMethodology] nvarchar(200) NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_CostDriver] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CostDriver_AllocMethodology] CHECK ([AllocMethodology] IN (N''DIRECT'', N''STEP_DOWN'', N''RECIPROCAL'', N''ABC'', N''TDABC'')),
    CONSTRAINT [CK_CostDriver_DriverType] CHECK ([DriverType] IN (N''TIME'', N''VOLUME'', N''VALUE'', N''QUANTITY'', N''AREA'', N''HEADCOUNT'', N''WEIGHTED'')),
    CONSTRAINT [CK_CostDriver_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [FK_CostDriver_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    CONSTRAINT [FK_CostDriver_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_CostDriver_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[CostDriverHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [CostPool] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NULL,
    [Classification] nvarchar(200) NOT NULL,
    [AllocMethod] nvarchar(200) NOT NULL,
    [PrimaryDriverId] uniqueidentifier NULL,
    [Status] nvarchar(200) NOT NULL,
    [Version] int NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_CostPool] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CostPool_AllocMethod] CHECK ([AllocMethod] IN (N''DIRECT'', N''STEP_DOWN'', N''RECIPROCAL'', N''ABC'', N''TDABC'')),
    CONSTRAINT [CK_CostPool_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [FK_CostPool_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_CostPool_CostDriver_PrimaryDriverId] FOREIGN KEY ([PrimaryDriverId]) REFERENCES [CostDriver] ([Id]),
    CONSTRAINT [FK_CostPool_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[CostPoolHistory]))');
GO

CREATE TABLE [DriverVolume] (
    [Id] uniqueidentifier NOT NULL,
    [DriverId] uniqueidentifier NOT NULL,
    [PeriodId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [ActivityId] uniqueidentifier NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_DriverVolume] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_DriverVolume_Activity_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [Activity] ([Id]),
    CONSTRAINT [FK_DriverVolume_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_DriverVolume_CostDriver_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [CostDriver] ([Id]),
    CONSTRAINT [FK_DriverVolume_Period_PeriodId] FOREIGN KEY ([PeriodId]) REFERENCES [Period] ([Id])
);
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [GLMapping] (
    [Id] uniqueidentifier NOT NULL,
    [GLAccountId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [CostPoolId] uniqueidentifier NOT NULL,
    [Weight] decimal(9,6) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_GLMapping] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_GLMapping_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_GLMapping_Weight] CHECK ([Weight] > 0 AND [Weight] <= 1),
    CONSTRAINT [FK_GLMapping_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_GLMapping_CostPool_CostPoolId] FOREIGN KEY ([CostPoolId]) REFERENCES [CostPool] ([Id]),
    CONSTRAINT [FK_GLMapping_GLAccount_GLAccountId] FOREIGN KEY ([GLAccountId]) REFERENCES [GLAccount] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[GLMappingHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Resource] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [ResourceType] nvarchar(200) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [IsCapacityDriven] bit NOT NULL,
    [CostPoolId] uniqueidentifier NULL,
    [IsActive] bit NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Resource] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Resource_ResourceType] CHECK ([ResourceType] IN (N''LABOR_PHYSICIAN'', N''LABOR_NURSE'', N''LABOR_TECH'', N''LABOR_ALLIED'', N''DRUG'', N''CONSUMABLE'', N''IMPLANT'', N''BLOOD'', N''EQUIPMENT'', N''ROOM'', N''UTILITY'', N''IT_SERVICE'', N''FACILITY'')),
    CONSTRAINT [FK_Resource_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_Resource_CostPool_CostPoolId] FOREIGN KEY ([CostPoolId]) REFERENCES [CostPool] ([Id]),
    CONSTRAINT [FK_Resource_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ResourceHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ProcedureResourceLine] (
    [Id] uniqueidentifier NOT NULL,
    [DefinitionId] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ProcedureResourceLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ProcedureResourceLine_CostingDefinition_DefinitionId] FOREIGN KEY ([DefinitionId]) REFERENCES [CostingDefinition] ([Id]),
    CONSTRAINT [FK_ProcedureResourceLine_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ProcedureResourceLineHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [Provider] (
    [Id] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Specialty] nvarchar(200) NOT NULL,
    [Grade] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_Provider] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Provider_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_Provider_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ProviderHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ResourceCapacity] (
    [Id] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [TheoreticalMinutes] decimal(19,6) NOT NULL,
    [PracticalRatio] decimal(9,6) NOT NULL,
    [PracticalMinutes] decimal(19,6) NOT NULL,
    [AnnualCost] decimal(19,4) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ResourceCapacity] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ResourceCapacity_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [FK_ResourceCapacity_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ResourceCapacityHistory]))');
GO

DECLARE @historyTableSchema sysname = SCHEMA_NAME()
EXEC(N'CREATE TABLE [ResourceRate] (
    [Id] uniqueidentifier NOT NULL,
    [ResourceId] uniqueidentifier NOT NULL,
    [UnitCost] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [Basis] nvarchar(200) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [Version] int NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [PeriodEnd] datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    [PeriodStart] datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    CONSTRAINT [PK_ResourceRate] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ResourceRate_EffectiveRange] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [FK_ResourceRate_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]),
    PERIOD FOR SYSTEM_TIME([PeriodStart], [PeriodEnd])
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [' + @historyTableSchema + N'].[ResourceRateHistory]))');
GO

CREATE TABLE [Encounter] (
    [Id] uniqueidentifier NOT NULL,
    [EncounterNo] nvarchar(200) NOT NULL,
    [PatientId] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [ServiceLineId] uniqueidentifier NULL,
    [PayerId] uniqueidentifier NOT NULL,
    [DrgId] uniqueidentifier NULL,
    [PackageId] uniqueidentifier NULL,
    [ProviderId] uniqueidentifier NULL,
    [EncType] nvarchar(200) NOT NULL,
    [AdmitAt] datetime2(3) NOT NULL,
    [DischargeAt] datetime2(3) NULL,
    [LengthOfStay] int NOT NULL,
    [IsSurgical] bit NOT NULL,
    [Acuity] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_Encounter] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Encounter_EncType] CHECK ([EncType] IN (N'IP', N'OP', N'ER', N'DAYCASE')),
    CONSTRAINT [FK_Encounter_DRG_DrgId] FOREIGN KEY ([DrgId]) REFERENCES [DRG] ([Id]),
    CONSTRAINT [FK_Encounter_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_Encounter_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_Encounter_Package_PackageId] FOREIGN KEY ([PackageId]) REFERENCES [Package] ([Id]),
    CONSTRAINT [FK_Encounter_Patient_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [Patient] ([Id]),
    CONSTRAINT [FK_Encounter_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id]),
    CONSTRAINT [FK_Encounter_Provider_ProviderId] FOREIGN KEY ([ProviderId]) REFERENCES [Provider] ([Id]),
    CONSTRAINT [FK_Encounter_ServiceLine_ServiceLineId] FOREIGN KEY ([ServiceLineId]) REFERENCES [ServiceLine] ([Id])
);
GO

CREATE TABLE [EncounterService] (
    [Id] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [ProviderId] uniqueidentifier NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [PerformedAt] datetime2(3) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_EncounterService] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_EncounterService_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_EncounterService_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_EncounterService_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id]),
    CONSTRAINT [FK_EncounterService_Provider_ProviderId] FOREIGN KEY ([ProviderId]) REFERENCES [Provider] ([Id])
);
GO

CREATE TABLE [RevenueTransaction] (
    [Id] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NULL,
    [PayerId] uniqueidentifier NOT NULL,
    [GrossAmount] decimal(19,4) NOT NULL,
    [Discount] decimal(19,4) NOT NULL,
    [NetAmount] decimal(19,4) NOT NULL,
    [PostedAt] datetime2(3) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_RevenueTransaction] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RevenueTransaction_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_RevenueTransaction_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id]),
    CONSTRAINT [FK_RevenueTransaction_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id])
);
GO

CREATE UNIQUE INDEX [IX_Activity_Code] ON [Activity] ([Code]);
GO

CREATE INDEX [IX_Activity_CostCenterId] ON [Activity] ([CostCenterId]);
GO

CREATE INDEX [IX_Activity_DepartmentId] ON [Activity] ([DepartmentId]);
GO

CREATE INDEX [IX_Activity_HospitalId] ON [Activity] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_ActivityCost_RunId_ActivityId] ON [ActivityCost] ([RunId], [ActivityId]);
GO

CREATE UNIQUE INDEX [IX_ActivityDriver_ActivityId_DriverId] ON [ActivityDriver] ([ActivityId], [DriverId]);
GO

CREATE INDEX [IX_ActivityDriver_DriverId] ON [ActivityDriver] ([DriverId]);
GO

CREATE UNIQUE INDEX [IX_ActivityResource_ActivityId_ResourceId] ON [ActivityResource] ([ActivityId], [ResourceId]);
GO

CREATE INDEX [IX_ActivityResource_ResourceId] ON [ActivityResource] ([ResourceId]);
GO

CREATE INDEX [IX_ActivityTransaction_ActivityId] ON [ActivityTransaction] ([ActivityId]);
GO

CREATE INDEX [IX_ActivityTransaction_CostCenterId] ON [ActivityTransaction] ([CostCenterId]);
GO

CREATE CLUSTERED INDEX [IX_ActivityTransaction_EncounterId_Id] ON [ActivityTransaction] ([EncounterId], [Id]);
GO

CREATE INDEX [IX_ActivityTransaction_EncounterServiceId] ON [ActivityTransaction] ([EncounterServiceId]);
GO

CREATE INDEX [IX_ActivityTransaction_OccurredAt] ON [ActivityTransaction] ([OccurredAt]);
GO

CREATE INDEX [IX_ActivityTransaction_ResourceId] ON [ActivityTransaction] ([ResourceId]);
GO

CREATE INDEX [IX_ActivityTransaction_SourceSystem_SourceRef] ON [ActivityTransaction] ([SourceSystem], [SourceRef]);
GO

CREATE INDEX [IX_AllocationResult_RunId_Stage] ON [AllocationResult] ([RunId], [Stage]);
GO

CREATE INDEX [IX_AllocationResult_TargetActivityId] ON [AllocationResult] ([TargetActivityId]);
GO

CREATE INDEX [IX_AllocationResult_TargetCostCenterId] ON [AllocationResult] ([TargetCostCenterId]);
GO

CREATE UNIQUE INDEX [IX_AllocationRule_Code] ON [AllocationRule] ([Code]);
GO

CREATE INDEX [IX_AllocationRule_DriverId] ON [AllocationRule] ([DriverId]);
GO

CREATE INDEX [IX_AllocationRule_SourcePoolId] ON [AllocationRule] ([SourcePoolId]);
GO

CREATE INDEX [IX_AllocationRuleTarget_CostCenterId] ON [AllocationRuleTarget] ([CostCenterId]);
GO

CREATE INDEX [IX_AllocationRuleTarget_RuleId] ON [AllocationRuleTarget] ([RuleId]);
GO

CREATE INDEX [IX_ApprovalRequest_CostingVersionId] ON [ApprovalRequest] ([CostingVersionId]);
GO

CREATE INDEX [IX_ApprovalRequest_EntityType_EntityId] ON [ApprovalRequest] ([EntityType], [EntityId]);
GO

CREATE INDEX [IX_ApprovalRequest_RequestedById] ON [ApprovalRequest] ([RequestedById]);
GO

CREATE INDEX [IX_ApprovalRequest_ReviewedById] ON [ApprovalRequest] ([ReviewedById]);
GO

CREATE INDEX [IX_AuditLog_ActorEmail] ON [AuditLog] ([ActorEmail]);
GO

CREATE INDEX [IX_AuditLog_At] ON [AuditLog] ([At]);
GO

CREATE INDEX [IX_AuditLog_EntityType_EntityId] ON [AuditLog] ([EntityType], [EntityId]);
GO

CREATE INDEX [IX_AuditLog_UserId] ON [AuditLog] ([UserId]);
GO

CREATE UNIQUE INDEX [IX_CalculationRun_Code] ON [CalculationRun] ([Code]);
GO

CREATE INDEX [IX_CalculationRun_CostingVersionId] ON [CalculationRun] ([CostingVersionId]);
GO

CREATE INDEX [IX_CalculationRun_HospitalId_PeriodId] ON [CalculationRun] ([HospitalId], [PeriodId]);
GO

CREATE INDEX [IX_CalculationRun_PeriodId] ON [CalculationRun] ([PeriodId]);
GO

CREATE INDEX [IX_CalculationRun_Status] ON [CalculationRun] ([Status]);
GO

CREATE UNIQUE INDEX [IX_CostCenter_Code] ON [CostCenter] ([Code]);
GO

CREATE INDEX [IX_CostCenter_DepartmentId] ON [CostCenter] ([DepartmentId]);
GO

CREATE INDEX [IX_CostCenter_FacilityId] ON [CostCenter] ([FacilityId]);
GO

CREATE INDEX [IX_CostCenter_HospitalId] ON [CostCenter] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_CostCenterBalance_RunId_CostCenterId] ON [CostCenterBalance] ([RunId], [CostCenterId]);
GO

CREATE INDEX [IX_CostComponent_EncounterId] ON [CostComponent] ([EncounterId]);
GO

CREATE INDEX [IX_CostComponent_ProcedureId] ON [CostComponent] ([ProcedureId]);
GO

CREATE INDEX [IX_CostComponent_RunId_ComponentType] ON [CostComponent] ([RunId], [ComponentType]);
GO

CREATE CLUSTERED INDEX [IX_CostComponent_RunId_Scope_ScopeId] ON [CostComponent] ([RunId], [Scope], [ScopeId]);
GO

CREATE INDEX [IX_CostDriver_ActivityId] ON [CostDriver] ([ActivityId]);
GO

CREATE UNIQUE INDEX [IX_CostDriver_Code] ON [CostDriver] ([Code]);
GO

CREATE INDEX [IX_CostDriver_CostCenterId] ON [CostDriver] ([CostCenterId]);
GO

CREATE INDEX [IX_CostDriver_CostPoolId] ON [CostDriver] ([CostPoolId]);
GO

CREATE INDEX [IX_CostDriver_HospitalId] ON [CostDriver] ([HospitalId]);
GO

CREATE INDEX [IX_CostingDefinition_CostingVersionId] ON [CostingDefinition] ([CostingVersionId]);
GO

CREATE UNIQUE INDEX [IX_CostingDefinition_ProcedureId_CostingVersionId] ON [CostingDefinition] ([ProcedureId], [CostingVersionId]);
GO

CREATE UNIQUE INDEX [IX_CostingVersion_Code] ON [CostingVersion] ([Code]);
GO

CREATE UNIQUE INDEX [IX_CostPool_Code] ON [CostPool] ([Code]);
GO

CREATE INDEX [IX_CostPool_CostCenterId] ON [CostPool] ([CostCenterId]);
GO

CREATE INDEX [IX_CostPool_HospitalId] ON [CostPool] ([HospitalId]);
GO

CREATE INDEX [IX_CostPool_PrimaryDriverId] ON [CostPool] ([PrimaryDriverId]);
GO

CREATE UNIQUE INDEX [IX_CostPoolBalance_RunId_CostPoolId] ON [CostPoolBalance] ([RunId], [CostPoolId]);
GO

CREATE INDEX [IX_DataQualityIssue_RunId_Severity] ON [DataQualityIssue] ([RunId], [Severity]);
GO

CREATE UNIQUE INDEX [IX_Department_Code] ON [Department] ([Code]);
GO

CREATE INDEX [IX_Department_HospitalId] ON [Department] ([HospitalId]);
GO

CREATE INDEX [IX_Department_ServiceLineId] ON [Department] ([ServiceLineId]);
GO

CREATE UNIQUE INDEX [IX_DRG_Code] ON [DRG] ([Code]);
GO

CREATE UNIQUE INDEX [IX_DrgCostSummary_RunId_DrgId] ON [DrgCostSummary] ([RunId], [DrgId]);
GO

CREATE INDEX [IX_DriverVolume_ActivityId] ON [DriverVolume] ([ActivityId]);
GO

CREATE INDEX [IX_DriverVolume_CostCenterId] ON [DriverVolume] ([CostCenterId]);
GO

CREATE INDEX [IX_DriverVolume_DriverId_PeriodId] ON [DriverVolume] ([DriverId], [PeriodId]);
GO

CREATE INDEX [IX_DriverVolume_PeriodId] ON [DriverVolume] ([PeriodId]);
GO

CREATE INDEX [IX_Encounter_DepartmentId] ON [Encounter] ([DepartmentId]);
GO

CREATE INDEX [IX_Encounter_DrgId] ON [Encounter] ([DrgId]);
GO

CREATE UNIQUE INDEX [IX_Encounter_EncounterNo] ON [Encounter] ([EncounterNo]);
GO

CREATE INDEX [IX_Encounter_HospitalId_AdmitAt] ON [Encounter] ([HospitalId], [AdmitAt]);
GO

CREATE INDEX [IX_Encounter_PackageId] ON [Encounter] ([PackageId]);
GO

CREATE INDEX [IX_Encounter_PatientId] ON [Encounter] ([PatientId]);
GO

CREATE INDEX [IX_Encounter_PayerId] ON [Encounter] ([PayerId]);
GO

CREATE INDEX [IX_Encounter_ProviderId] ON [Encounter] ([ProviderId]);
GO

CREATE INDEX [IX_Encounter_ServiceLineId] ON [Encounter] ([ServiceLineId]);
GO

CREATE UNIQUE CLUSTERED INDEX [IX_EncounterCost_RunId_EncounterId] ON [EncounterCost] ([RunId], [EncounterId]);
GO

CREATE INDEX [IX_EncounterService_CostCenterId] ON [EncounterService] ([CostCenterId]);
GO

CREATE INDEX [IX_EncounterService_EncounterId] ON [EncounterService] ([EncounterId]);
GO

CREATE INDEX [IX_EncounterService_ProcedureId] ON [EncounterService] ([ProcedureId]);
GO

CREATE INDEX [IX_EncounterService_ProviderId] ON [EncounterService] ([ProviderId]);
GO

CREATE UNIQUE INDEX [IX_Facility_Code] ON [Facility] ([Code]);
GO

CREATE INDEX [IX_Facility_HospitalId] ON [Facility] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_GLAccount_Code] ON [GLAccount] ([Code]);
GO

CREATE INDEX [IX_GLMapping_CostCenterId] ON [GLMapping] ([CostCenterId]);
GO

CREATE INDEX [IX_GLMapping_CostPoolId] ON [GLMapping] ([CostPoolId]);
GO

CREATE INDEX [IX_GLMapping_GLAccountId_CostCenterId] ON [GLMapping] ([GLAccountId], [CostCenterId]);
GO

CREATE INDEX [IX_GLPosting_CostCenterId] ON [GLPosting] ([CostCenterId]);
GO

CREATE INDEX [IX_GLPosting_GLAccountId] ON [GLPosting] ([GLAccountId]);
GO

CREATE INDEX [IX_GLPosting_HospitalId] ON [GLPosting] ([HospitalId]);
GO

CREATE CLUSTERED INDEX [IX_GLPosting_PeriodId_HospitalId] ON [GLPosting] ([PeriodId], [HospitalId]);
GO

CREATE UNIQUE INDEX [IX_Hospital_Code] ON [Hospital] ([Code]);
GO

CREATE UNIQUE INDEX [IX_Package_Code] ON [Package] ([Code]);
GO

CREATE INDEX [IX_Package_HospitalId] ON [Package] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_PackageComponent_PackageId_ProcedureId] ON [PackageComponent] ([PackageId], [ProcedureId]);
GO

CREATE INDEX [IX_PackageComponent_ProcedureId] ON [PackageComponent] ([ProcedureId]);
GO

CREATE UNIQUE INDEX [IX_PackageCost_RunId_PackageId] ON [PackageCost] ([RunId], [PackageId]);
GO

CREATE INDEX [IX_Patient_HospitalId] ON [Patient] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_Patient_Mrn] ON [Patient] ([Mrn]);
GO

CREATE UNIQUE INDEX [IX_Payer_Code] ON [Payer] ([Code]);
GO

CREATE UNIQUE INDEX [IX_Period_Code] ON [Period] ([Code]);
GO

CREATE UNIQUE INDEX [IX_Procedure_Code] ON [Procedure] ([Code]);
GO

CREATE INDEX [IX_Procedure_DepartmentId] ON [Procedure] ([DepartmentId]);
GO

CREATE INDEX [IX_Procedure_HospitalId] ON [Procedure] ([HospitalId]);
GO

CREATE INDEX [IX_Procedure_ServiceLineId] ON [Procedure] ([ServiceLineId]);
GO

CREATE INDEX [IX_ProcedureActivityLine_ActivityId] ON [ProcedureActivityLine] ([ActivityId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureActivityLine_DefinitionId_ActivityId] ON [ProcedureActivityLine] ([DefinitionId], [ActivityId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureCost_RunId_ProcedureId_CostType] ON [ProcedureCost] ([RunId], [ProcedureId], [CostType]);
GO

CREATE INDEX [IX_ProcedureRate_HospitalId] ON [ProcedureRate] ([HospitalId]);
GO

CREATE INDEX [IX_ProcedureRate_ProcedureId] ON [ProcedureRate] ([ProcedureId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureResourceLine_DefinitionId_ResourceId] ON [ProcedureResourceLine] ([DefinitionId], [ResourceId]);
GO

CREATE INDEX [IX_ProcedureResourceLine_ResourceId] ON [ProcedureResourceLine] ([ResourceId]);
GO

CREATE UNIQUE INDEX [IX_Provider_Code] ON [Provider] ([Code]);
GO

CREATE INDEX [IX_Provider_DepartmentId] ON [Provider] ([DepartmentId]);
GO

CREATE INDEX [IX_Provider_ResourceId] ON [Provider] ([ResourceId]);
GO

CREATE INDEX [IX_Reconciliation_RunId] ON [Reconciliation] ([RunId]);
GO

CREATE INDEX [IX_ReconciliationLine_ReconciliationId] ON [ReconciliationLine] ([ReconciliationId]);
GO

CREATE INDEX [IX_ResidualBucket_RunId] ON [ResidualBucket] ([RunId]);
GO

CREATE UNIQUE INDEX [IX_Resource_Code] ON [Resource] ([Code]);
GO

CREATE INDEX [IX_Resource_CostCenterId] ON [Resource] ([CostCenterId]);
GO

CREATE INDEX [IX_Resource_CostPoolId] ON [Resource] ([CostPoolId]);
GO

CREATE INDEX [IX_Resource_HospitalId] ON [Resource] ([HospitalId]);
GO

CREATE INDEX [IX_ResourceCapacity_ResourceId] ON [ResourceCapacity] ([ResourceId]);
GO

CREATE INDEX [IX_ResourceRate_ResourceId_EffectiveFrom] ON [ResourceRate] ([ResourceId], [EffectiveFrom]);
GO

CREATE UNIQUE INDEX [IX_ResourceUnitCost_RunId_ResourceId] ON [ResourceUnitCost] ([RunId], [ResourceId]);
GO

CREATE INDEX [IX_RevenueTransaction_EncounterId] ON [RevenueTransaction] ([EncounterId]);
GO

CREATE INDEX [IX_RevenueTransaction_PayerId] ON [RevenueTransaction] ([PayerId]);
GO

CREATE INDEX [IX_RevenueTransaction_ProcedureId] ON [RevenueTransaction] ([ProcedureId]);
GO

CREATE INDEX [IX_RunStep_RunId] ON [RunStep] ([RunId]);
GO

CREATE INDEX [IX_Scenario_BaseRunId] ON [Scenario] ([BaseRunId]);
GO

CREATE UNIQUE INDEX [IX_Scenario_Code] ON [Scenario] ([Code]);
GO

CREATE INDEX [IX_ScenarioLever_ScenarioId] ON [ScenarioLever] ([ScenarioId]);
GO

CREATE INDEX [IX_ScenarioResult_ScenarioId] ON [ScenarioResult] ([ScenarioId]);
GO

CREATE UNIQUE INDEX [IX_ServiceLine_Code] ON [ServiceLine] ([Code]);
GO

CREATE UNIQUE INDEX [IX_User_Email] ON [User] ([Email]);
GO

CREATE INDEX [IX_Variance_RunId_Scope] ON [Variance] ([RunId], [Scope]);
GO

CREATE INDEX [IX_VarianceComponent_VarianceId] ON [VarianceComponent] ([VarianceId]);
GO

ALTER TABLE [ActivityDriver] ADD CONSTRAINT [FK_ActivityDriver_CostDriver_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [CostDriver] ([Id]);
GO

ALTER TABLE [ActivityResource] ADD CONSTRAINT [FK_ActivityResource_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]);
GO

ALTER TABLE [ActivityTransaction] ADD CONSTRAINT [FK_ActivityTransaction_EncounterService_EncounterServiceId] FOREIGN KEY ([EncounterServiceId]) REFERENCES [EncounterService] ([Id]);
GO

ALTER TABLE [ActivityTransaction] ADD CONSTRAINT [FK_ActivityTransaction_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]);
GO

ALTER TABLE [ActivityTransaction] ADD CONSTRAINT [FK_ActivityTransaction_Resource_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resource] ([Id]);
GO

ALTER TABLE [AllocationRule] ADD CONSTRAINT [FK_AllocationRule_CostDriver_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [CostDriver] ([Id]);
GO

ALTER TABLE [AllocationRule] ADD CONSTRAINT [FK_AllocationRule_CostPool_SourcePoolId] FOREIGN KEY ([SourcePoolId]) REFERENCES [CostPool] ([Id]);
GO

ALTER TABLE [CostDriver] ADD CONSTRAINT [FK_CostDriver_CostPool_CostPoolId] FOREIGN KEY ([CostPoolId]) REFERENCES [CostPool] ([Id]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE FUNCTION dbo.fn_ResourceRateOverlaps(@ResourceId UNIQUEIDENTIFIER, @Id UNIQUEIDENTIFIER, @From DATE, @To DATE)
RETURNS INT WITH SCHEMABINDING AS
BEGIN
    RETURN (SELECT COUNT(*) FROM dbo.ResourceRate r
            WHERE r.ResourceId = @ResourceId AND r.Id <> @Id
              AND r.EffectiveFrom <= ISNULL(@To, '9999-12-31')
              AND ISNULL(r.EffectiveTo, '9999-12-31') >= @From);
END
GO

ALTER TABLE dbo.ResourceRate WITH CHECK ADD CONSTRAINT CK_ResourceRate_NoOverlap
    CHECK (dbo.fn_ResourceRateOverlaps(ResourceId, Id, EffectiveFrom, EffectiveTo) = 0);
GO

CREATE VIEW dbo.vw_PublishedEncounterCost AS
SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, v.Code AS CostingVersion, ec.*
FROM dbo.EncounterCost ec
JOIN dbo.CalculationRun r ON r.Id = ec.RunId AND r.Status = 'PUBLISHED'
JOIN dbo.Hospital h ON h.Id = r.HospitalId
JOIN dbo.Period p ON p.Id = r.PeriodId
JOIN dbo.CostingVersion v ON v.Id = r.CostingVersionId
GO

CREATE VIEW dbo.vw_PublishedCostComponent AS
SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, cc.*
FROM dbo.CostComponent cc
JOIN dbo.CalculationRun r ON r.Id = cc.RunId AND r.Status = 'PUBLISHED'
JOIN dbo.Hospital h ON h.Id = r.HospitalId
JOIN dbo.Period p ON p.Id = r.PeriodId
GO

CREATE VIEW dbo.vw_PublishedProcedureCost AS
SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, pr.Code AS ProcedureCode, pr.CatalogCode, pc.*
FROM dbo.ProcedureCost pc
JOIN dbo.CalculationRun r ON r.Id = pc.RunId AND r.Status = 'PUBLISHED'
JOIN dbo.Hospital h ON h.Id = r.HospitalId
JOIN dbo.Period p ON p.Id = r.PeriodId
JOIN dbo.[Procedure] pr ON pr.Id = pc.ProcedureId
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Hospital] ADD [HisProjectId] int NULL;
GO

ALTER TABLE [Hospital] ADD [HisSetupId] nvarchar(200) NULL;
GO

CREATE INDEX [IX_Hospital_HisSetupId_HisProjectId] ON [Hospital] ([HisSetupId], [HisProjectId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Period] ADD [HospitalId] uniqueidentifier NULL;
GO

CREATE INDEX [IX_Period_HospitalId] ON [Period] ([HospitalId]);
GO

ALTER TABLE [Period] ADD CONSTRAINT [FK_Period_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ProcedureTypeCosting] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [ProcType] nvarchar(200) NOT NULL,
    [MarginPct] decimal(9,6) NOT NULL,
    CONSTRAINT [PK_ProcedureTypeCosting] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ProcedureTypeCosting_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE UNIQUE INDEX [IX_ProcedureTypeCosting_HospitalId_ProcType] ON [ProcedureTypeCosting] ([HospitalId], [ProcType]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ProcedureMargin] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [MarginPct] decimal(9,6) NOT NULL,
    CONSTRAINT [PK_ProcedureMargin] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ProcedureMargin_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_ProcedureMargin_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id])
);
GO

CREATE INDEX [IX_ProcedureMargin_HospitalId] ON [ProcedureMargin] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureMargin_ProcedureId] ON [ProcedureMargin] ([ProcedureId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_CostCenter_Code] ON [CostCenter];
GO

DROP INDEX [IX_CostCenter_HospitalId] ON [CostCenter];
GO

UPDATE cc
SET Code = SUBSTRING(cc.Code, LEN(h.Code) + 2, 200)
FROM CostCenter cc
INNER JOIN Hospital h ON h.Id = cc.HospitalId
WHERE cc.Code LIKE h.Code + '.%';
GO

CREATE UNIQUE INDEX [IX_CostCenter_HospitalId_Code] ON [CostCenter] ([HospitalId], [Code]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_ProcedureTypeCosting_HospitalId_ProcType] ON [ProcedureTypeCosting];
GO

DROP INDEX [IX_ProcedureMargin_ProcedureId] ON [ProcedureMargin];
GO

ALTER TABLE [ProcedureTypeCosting] ADD [Stream] nvarchar(20) NOT NULL DEFAULT N'DIRECT';
GO

ALTER TABLE [ProcedureMargin] ADD [Stream] nvarchar(20) NOT NULL DEFAULT N'DIRECT';
GO

CREATE UNIQUE INDEX [IX_ProcedureTypeCosting_HospitalId_ProcType_Stream] ON [ProcedureTypeCosting] ([HospitalId], [ProcType], [Stream]);
GO

ALTER TABLE [ProcedureTypeCosting] ADD CONSTRAINT [CK_ProcedureTypeCosting_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT'));
GO

CREATE UNIQUE INDEX [IX_ProcedureMargin_ProcedureId_Stream] ON [ProcedureMargin] ([ProcedureId], [Stream]);
GO

ALTER TABLE [ProcedureMargin] ADD CONSTRAINT [CK_ProcedureMargin_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT'));
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [EncounterOverhead] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [CostCenterId] uniqueidentifier NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [OverheadPct] decimal(9,6) NOT NULL,
    [OverheadSar] decimal(19,4) NOT NULL,
    CONSTRAINT [PK_EncounterOverhead] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_EncounterOverhead_CostCenter_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [CostCenter] ([Id]),
    CONSTRAINT [FK_EncounterOverhead_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE INDEX [IX_EncounterOverhead_CostCenterId] ON [EncounterOverhead] ([CostCenterId]);
GO

CREATE INDEX [IX_EncounterOverhead_HospitalId_CostCenterId] ON [EncounterOverhead] ([HospitalId], [CostCenterId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

EXEC sp_rename N'[EncounterOverhead].[OverheadSar]', N'OverheadAmount', N'COLUMN';
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_ProcedureTypeCosting_HospitalId_ProcType_Stream] ON [ProcedureTypeCosting];
GO

DROP INDEX [IX_ProcedureMargin_ProcedureId_Stream] ON [ProcedureMargin];
GO

DROP INDEX [IX_EncounterOverhead_HospitalId_CostCenterId] ON [EncounterOverhead];
GO

ALTER TABLE [ProcedureTypeCosting] ADD [CostingVersionId] uniqueidentifier NULL;
GO

ALTER TABLE [ProcedureMargin] ADD [CostingVersionId] uniqueidentifier NULL;
GO

ALTER TABLE [EncounterOverhead] ADD [CostingVersionId] uniqueidentifier NULL;
GO

WITH HospitalVersion AS (
    SELECT HospitalId, CostingVersionId
    FROM (
        SELECT r.HospitalId, r.CostingVersionId,
               ROW_NUMBER() OVER (
                   PARTITION BY r.HospitalId
                   ORDER BY CASE WHEN r.Status = N'PUBLISHED' THEN 0 ELSE 1 END,
                            r.CreatedAt DESC) AS rn
        FROM CalculationRun r
        WHERE r.RunType <> N'SIMULATION'
    ) x
    WHERE rn = 1
),
Fallback AS (
    SELECT TOP 1 Id AS CostingVersionId
    FROM CostingVersion
    ORDER BY CASE WHEN Status = N'PUBLISHED' THEN 0 ELSE 1 END, CreatedAt DESC
)UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
FROM ProcedureTypeCosting p
CROSS JOIN Fallback f
LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
WHERE p.CostingVersionId IS NULL
GO

WITH HospitalVersion AS (
    SELECT HospitalId, CostingVersionId
    FROM (
        SELECT r.HospitalId, r.CostingVersionId,
               ROW_NUMBER() OVER (
                   PARTITION BY r.HospitalId
                   ORDER BY CASE WHEN r.Status = N'PUBLISHED' THEN 0 ELSE 1 END,
                            r.CreatedAt DESC) AS rn
        FROM CalculationRun r
        WHERE r.RunType <> N'SIMULATION'
    ) x
    WHERE rn = 1
),
Fallback AS (
    SELECT TOP 1 Id AS CostingVersionId
    FROM CostingVersion
    ORDER BY CASE WHEN Status = N'PUBLISHED' THEN 0 ELSE 1 END, CreatedAt DESC
)UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
FROM ProcedureMargin p
CROSS JOIN Fallback f
LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
WHERE p.CostingVersionId IS NULL
GO

WITH HospitalVersion AS (
    SELECT HospitalId, CostingVersionId
    FROM (
        SELECT r.HospitalId, r.CostingVersionId,
               ROW_NUMBER() OVER (
                   PARTITION BY r.HospitalId
                   ORDER BY CASE WHEN r.Status = N'PUBLISHED' THEN 0 ELSE 1 END,
                            r.CreatedAt DESC) AS rn
        FROM CalculationRun r
        WHERE r.RunType <> N'SIMULATION'
    ) x
    WHERE rn = 1
),
Fallback AS (
    SELECT TOP 1 Id AS CostingVersionId
    FROM CostingVersion
    ORDER BY CASE WHEN Status = N'PUBLISHED' THEN 0 ELSE 1 END, CreatedAt DESC
)UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
FROM EncounterOverhead p
CROSS JOIN Fallback f
LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
WHERE p.CostingVersionId IS NULL
GO

DECLARE @var0 sysname;
SELECT @var0 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ProcedureTypeCosting]') AND [c].[name] = N'CostingVersionId');
IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [ProcedureTypeCosting] DROP CONSTRAINT [' + @var0 + '];');
ALTER TABLE [ProcedureTypeCosting] ALTER COLUMN [CostingVersionId] uniqueidentifier NOT NULL;
GO

DECLARE @var1 sysname;
SELECT @var1 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ProcedureMargin]') AND [c].[name] = N'CostingVersionId');
IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [ProcedureMargin] DROP CONSTRAINT [' + @var1 + '];');
ALTER TABLE [ProcedureMargin] ALTER COLUMN [CostingVersionId] uniqueidentifier NOT NULL;
GO

DECLARE @var2 sysname;
SELECT @var2 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EncounterOverhead]') AND [c].[name] = N'CostingVersionId');
IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [EncounterOverhead] DROP CONSTRAINT [' + @var2 + '];');
ALTER TABLE [EncounterOverhead] ALTER COLUMN [CostingVersionId] uniqueidentifier NOT NULL;
GO

CREATE INDEX [IX_ProcedureTypeCosting_CostingVersionId] ON [ProcedureTypeCosting] ([CostingVersionId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureTypeCosting_HospitalId_CostingVersionId_ProcType_Stream] ON [ProcedureTypeCosting] ([HospitalId], [CostingVersionId], [ProcType], [Stream]);
GO

CREATE INDEX [IX_ProcedureMargin_CostingVersionId] ON [ProcedureMargin] ([CostingVersionId]);
GO

CREATE UNIQUE INDEX [IX_ProcedureMargin_ProcedureId_CostingVersionId_Stream] ON [ProcedureMargin] ([ProcedureId], [CostingVersionId], [Stream]);
GO

CREATE INDEX [IX_EncounterOverhead_CostingVersionId] ON [EncounterOverhead] ([CostingVersionId]);
GO

CREATE INDEX [IX_EncounterOverhead_HospitalId_CostingVersionId_CostCenterId] ON [EncounterOverhead] ([HospitalId], [CostingVersionId], [CostCenterId]);
GO

ALTER TABLE [EncounterOverhead] ADD CONSTRAINT [FK_EncounterOverhead_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]);
GO

ALTER TABLE [ProcedureMargin] ADD CONSTRAINT [FK_ProcedureMargin_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]);
GO

ALTER TABLE [ProcedureTypeCosting] ADD CONSTRAINT [FK_ProcedureTypeCosting_CostingVersion_CostingVersionId] FOREIGN KEY ([CostingVersionId]) REFERENCES [CostingVersion] ([Id]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_Encounter_EncounterNo] ON [Encounter];
GO

UPDATE e
SET EncounterNo = CASE
    WHEN LEFT(x.Local, 3) IN ('OP-', 'IP-', 'ER-') THEN SUBSTRING(x.Local, 4, LEN(x.Local))
    ELSE x.Local END
FROM Encounter e
JOIN Hospital h ON h.Id = e.HospitalId
CROSS APPLY (SELECT SUBSTRING(e.EncounterNo, LEN(h.Code) + 2, LEN(e.EncounterNo)) AS Local) x
WHERE LEFT(e.EncounterNo, LEN(h.Code) + 1) = h.Code + '-';
GO

CREATE UNIQUE INDEX [IX_Encounter_HospitalId_EncounterNo] ON [Encounter] ([HospitalId], [EncounterNo]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_Patient_HospitalId] ON [Patient];
GO

DROP INDEX [IX_Patient_Mrn] ON [Patient];
GO

DECLARE @var3 sysname;
SELECT @var3 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Provider]') AND [c].[name] = N'ResourceId');
IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [Provider] DROP CONSTRAINT [' + @var3 + '];');
ALTER TABLE [Provider] ALTER COLUMN [ResourceId] uniqueidentifier NULL;
GO

CREATE UNIQUE INDEX [IX_Patient_HospitalId_Mrn] ON [Patient] ([HospitalId], [Mrn]);
GO

INSERT INTO Patient (Id, Mrn, HospitalId, FullName, BirthYear, Sex, Segment, NationalIdMasked)
SELECT NEWID(), p.Mrn, x.HospitalId, p.FullName, p.BirthYear, p.Sex, p.Segment, p.NationalIdMasked
FROM (SELECT DISTINCT e.PatientId, e.HospitalId FROM Encounter e JOIN Patient q ON q.Id = e.PatientId WHERE q.HospitalId <> e.HospitalId) x
JOIN Patient p ON p.Id = x.PatientId;

UPDATE e SET PatientId = c.Id
FROM Encounter e
JOIN Patient q ON q.Id = e.PatientId AND q.HospitalId <> e.HospitalId
JOIN Patient c ON c.Mrn = q.Mrn AND c.HospitalId = e.HospitalId;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [CalculationRun] ADD [CostingMethod] nvarchar(200) NOT NULL DEFAULT N'';
GO

CREATE TABLE [RunDimensionCost] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [Dimension] nvarchar(32) NOT NULL,
    [Key] nvarchar(200) NOT NULL,
    [Label] nvarchar(200) NOT NULL,
    [Encounters] int NOT NULL,
    [Revenue] decimal(19,4) NOT NULL,
    [DirectCost] decimal(19,4) NOT NULL,
    [IndirectCost] decimal(19,4) NOT NULL,
    [OverheadCost] decimal(19,4) NOT NULL,
    [TotalCost] decimal(19,4) NOT NULL,
    [ContributionMargin] decimal(19,4) NOT NULL,
    [OperatingMargin] decimal(19,4) NOT NULL,
    [MarginPct] decimal(19,6) NOT NULL,
    CONSTRAINT [PK_RunDimensionCost] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RunDimensionCost_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_RunDimensionCost_RunId_Dimension] ON [RunDimensionCost] ([RunId], [Dimension]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_CostingVersion_Code] ON [CostingVersion];
GO

ALTER TABLE [CostingVersion] ADD [HospitalId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
GO

CREATE TABLE #VersionMap (OldId uniqueidentifier NOT NULL, HospitalId uniqueidentifier NOT NULL, NewId uniqueidentifier NOT NULL, PRIMARY KEY (OldId, HospitalId));

WITH Ranked AS (
    SELECT v.Id AS OldId, h.Id AS HospitalId,
           ROW_NUMBER() OVER (PARTITION BY v.Id ORDER BY s.Setup DESC, d.Defs DESC, h.Code) AS Rn
    FROM CostingVersion v
    CROSS JOIN Hospital h
    CROSS APPLY (SELECT (SELECT COUNT(*) FROM ProcedureTypeCosting x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id)
                      + (SELECT COUNT(*) FROM ProcedureMargin x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id)
                      + (SELECT COUNT(*) FROM EncounterOverhead x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id) AS Setup) s
    CROSS APPLY (SELECT COUNT(*) AS Defs FROM CostingDefinition x JOIN [Procedure] p ON p.Id = x.ProcedureId
                 WHERE x.CostingVersionId = v.Id AND p.HospitalId = h.Id) d
    WHERE v.HospitalId = '00000000-0000-0000-0000-000000000000'
)
INSERT #VersionMap (OldId, HospitalId, NewId)
SELECT OldId, HospitalId, CASE WHEN Rn = 1 THEN OldId ELSE NEWID() END FROM Ranked;

INSERT CostingVersion (Id, HospitalId, Code, Name, Description, EffectiveFrom, EffectiveTo, Status, PublishedAt, PublishedById, CreatedAt)
SELECT m.NewId, m.HospitalId, v.Code, v.Name, v.Description, v.EffectiveFrom, v.EffectiveTo, v.Status, v.PublishedAt, v.PublishedById, v.CreatedAt
FROM #VersionMap m JOIN CostingVersion v ON v.Id = m.OldId WHERE m.NewId <> m.OldId;

UPDATE v SET HospitalId = m.HospitalId
FROM CostingVersion v JOIN #VersionMap m ON m.OldId = v.Id AND m.NewId = v.Id;

UPDATE d SET CostingVersionId = m.NewId
FROM CostingDefinition d JOIN [Procedure] p ON p.Id = d.ProcedureId
JOIN #VersionMap m ON m.OldId = d.CostingVersionId AND m.HospitalId = p.HospitalId WHERE m.NewId <> m.OldId;

UPDATE x SET CostingVersionId = m.NewId
FROM ProcedureTypeCosting x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

UPDATE x SET CostingVersionId = m.NewId
FROM ProcedureMargin x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

UPDATE x SET CostingVersionId = m.NewId
FROM EncounterOverhead x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

UPDATE a SET CostingVersionId = m.NewId
FROM ApprovalRequest a JOIN CalculationRun r ON r.Id = a.EntityId
JOIN #VersionMap m ON m.OldId = a.CostingVersionId AND m.HospitalId = r.HospitalId
WHERE a.EntityType = 'CALCULATION_RUN' AND m.NewId <> m.OldId;

UPDATE x SET CostingVersionId = m.NewId
FROM CalculationRun x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

INSERT ApprovalRequest (Id, EntityType, EntityId, EntityLabel, CostingVersionId, Stage, RequestedById, ReviewedById, RequestNote, ReviewNote, RequestedAt, ReviewedAt)
SELECT NEWID(), a.EntityType, m.NewId, a.EntityLabel, m.NewId, a.Stage, a.RequestedById, a.ReviewedById, a.RequestNote, a.ReviewNote, a.RequestedAt, a.ReviewedAt
FROM ApprovalRequest a JOIN #VersionMap m ON m.OldId = a.EntityId
WHERE a.EntityType = 'COSTING_VERSION' AND m.NewId <> m.OldId;

DROP TABLE #VersionMap;
GO

CREATE UNIQUE INDEX [IX_CostingVersion_HospitalId_Code] ON [CostingVersion] ([HospitalId], [Code]);
GO

ALTER TABLE [CostingVersion] ADD CONSTRAINT [FK_CostingVersion_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Encounter] ADD [SourceAssessmentNo] nvarchar(200) NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [RefundTransaction] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(200) NULL,
    [PatientMrn] nvarchar(200) NULL,
    [ProcedureId] uniqueidentifier NULL,
    [HisProcedureId] nvarchar(200) NULL,
    [PayerId] uniqueidentifier NULL,
    [RefundNo] nvarchar(200) NOT NULL,
    [RefundType] int NOT NULL,
    [RefundDate] datetime2(3) NOT NULL,
    [OriginalTransactionType] int NOT NULL,
    [OriginalInvoiceNo] nvarchar(200) NOT NULL,
    [OriginalInvoiceDate] datetime2(3) NULL,
    [InvoiceLineNo] int NOT NULL,
    [InvoiceCancelledAt] datetime2(3) NULL,
    [RefundAmount] decimal(19,4) NOT NULL,
    [PatientShare] decimal(19,4) NOT NULL,
    [CompanyShare] decimal(19,4) NOT NULL,
    [PatientPaid] decimal(19,4) NOT NULL,
    [TaxAmount] decimal(19,4) NOT NULL,
    [HeaderRefundAmount] decimal(19,4) NOT NULL,
    [HeaderAmountPaid] decimal(19,4) NOT NULL,
    [Status] int NOT NULL,
    [ApprovalStatus] int NULL,
    [ApprovedAt] datetime2(3) NULL,
    [CancelledAt] datetime2(3) NULL,
    [ReasonId] int NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_RefundTransaction] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RefundTransaction_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_RefundTransaction_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_RefundTransaction_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id]),
    CONSTRAINT [FK_RefundTransaction_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id])
);
GO

CREATE INDEX [IX_RefundTransaction_EncounterId] ON [RefundTransaction] ([EncounterId]);
GO

CREATE UNIQUE INDEX [IX_RefundTransaction_HospitalId_SourceRef] ON [RefundTransaction] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_RefundTransaction_PayerId] ON [RefundTransaction] ([PayerId]);
GO

CREATE INDEX [IX_RefundTransaction_ProcedureId] ON [RefundTransaction] ([ProcedureId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RevenueTransaction] ADD [HisLineStatus] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsPerformed] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsRefunded] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsReturned] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [PerformType] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [RefundedAmount] decimal(19,4) NULL;
GO

ALTER TABLE [Encounter] ADD [HisArrivedAt] datetime2(3) NULL;
GO

ALTER TABLE [Encounter] ADD [HisErStatus] int NULL;
GO

ALTER TABLE [Encounter] ADD [HisStatus] int NULL;
GO

ALTER TABLE [Encounter] ADD [HisVisitClosed] bit NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RevenueTransaction] ADD [BillType] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [ClaimedAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [CompanyShare] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [CompanyTaxAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [DiscountType] nvarchar(200) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [HisDiscountAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [HisQuantity] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [HisUnitPrice] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsAcceptedWithDrg] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsPackageInvoice] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsPackageSelected] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsZeroPrice] bit NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [OutlierPayment] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [PackageProcedureId] nvarchar(200) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [PackageSequenceNo] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [PatientShare] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [PatientTaxAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [RejectedAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [SettledAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [TaxAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [WaivedAmount] decimal(19,4) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [WaiverId] int NULL;
GO

CREATE TABLE [InvoicePackageItem] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(200) NULL,
    [ProcedureId] uniqueidentifier NULL,
    [HisProcedureId] nvarchar(200) NULL,
    [PackageProcedureId] nvarchar(200) NULL,
    [PackageLineItemNo] int NULL,
    [PackageSequence] int NULL,
    [TransactionType] int NOT NULL,
    [InvoiceNo] nvarchar(200) NOT NULL,
    [LineItemNo] int NOT NULL,
    [InvoiceDate] datetime2(3) NOT NULL,
    [Price] decimal(19,4) NOT NULL,
    [DiscountedCharge] decimal(19,4) NOT NULL,
    [PatientShare] decimal(19,4) NOT NULL,
    [PatientPaid] decimal(19,4) NOT NULL,
    [CompanyShare] decimal(19,4) NOT NULL,
    [CompanyPaid] decimal(19,4) NOT NULL,
    [ChildInvoiceNo] nvarchar(200) NULL,
    [ChildInvoiceLineItemNo] int NULL,
    [IsCompleted] bit NULL,
    [IsRefunded] bit NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_InvoicePackageItem] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_InvoicePackageItem_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_InvoicePackageItem_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_InvoicePackageItem_Procedure_ProcedureId] FOREIGN KEY ([ProcedureId]) REFERENCES [Procedure] ([Id])
);
GO

CREATE INDEX [IX_InvoicePackageItem_EncounterId] ON [InvoicePackageItem] ([EncounterId]);
GO

CREATE UNIQUE INDEX [IX_InvoicePackageItem_HospitalId_SourceRef] ON [InvoicePackageItem] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_InvoicePackageItem_ProcedureId] ON [InvoicePackageItem] ([ProcedureId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Payer] ADD [AdministratorId] uniqueidentifier NULL;
GO

ALTER TABLE [Payer] ADD [InsurerId] uniqueidentifier NULL;
GO

CREATE TABLE [Insurer] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_Insurer] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [PayerAdministrator] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_PayerAdministrator] PRIMARY KEY ([Id])
);
GO

CREATE INDEX [IX_Payer_AdministratorId] ON [Payer] ([AdministratorId]);
GO

CREATE INDEX [IX_Payer_InsurerId] ON [Payer] ([InsurerId]);
GO

CREATE UNIQUE INDEX [IX_Insurer_Code] ON [Insurer] ([Code]);
GO

CREATE UNIQUE INDEX [IX_PayerAdministrator_Code] ON [PayerAdministrator] ([Code]);
GO

ALTER TABLE [Payer] ADD CONSTRAINT [FK_Payer_Insurer_InsurerId] FOREIGN KEY ([InsurerId]) REFERENCES [Insurer] ([Id]);
GO

ALTER TABLE [Payer] ADD CONSTRAINT [FK_Payer_PayerAdministrator_AdministratorId] FOREIGN KEY ([AdministratorId]) REFERENCES [PayerAdministrator] ([Id]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_Period_Code] ON [Period];
GO

DROP INDEX [IX_Period_HospitalId] ON [Period];
GO

CREATE UNIQUE INDEX [IX_Period_HospitalId_Code] ON [Period] ([HospitalId], [Code]) WHERE [HospitalId] IS NOT NULL;
GO

SELECT p.Id AS SharedId, h.Id AS HospitalId, NEWID() AS OwnId INTO #Owned
FROM [Period] p CROSS JOIN [Hospital] h
WHERE p.HospitalId IS NULL AND (
      EXISTS (SELECT 1 FROM [CalculationRun] r WHERE r.PeriodId = p.Id AND r.HospitalId = h.Id)
   OR EXISTS (SELECT 1 FROM [GLPosting] g WHERE g.PeriodId = p.Id AND g.HospitalId = h.Id)
   OR EXISTS (SELECT 1 FROM [DriverVolume] d JOIN [CostCenter] c ON c.Id = d.CostCenterId WHERE d.PeriodId = p.Id AND c.HospitalId = h.Id)
   OR (NOT EXISTS (SELECT 1 FROM [Period] own WHERE own.HospitalId = h.Id)
       AND EXISTS (SELECT 1 FROM [Encounter] e WHERE e.HospitalId = h.Id AND e.AdmitAt >= p.StartDate AND e.AdmitAt < DATEADD(day, 1, CAST(p.EndDate AS datetime2)))));
INSERT [Period] (Id, Code, StartDate, EndDate, Days, Status, HospitalId)
SELECT o.OwnId, p.Code, p.StartDate, p.EndDate, p.Days, p.Status, o.HospitalId FROM #Owned o JOIN [Period] p ON p.Id = o.SharedId;
UPDATE r SET PeriodId = o.OwnId FROM [CalculationRun] r JOIN #Owned o ON o.SharedId = r.PeriodId AND o.HospitalId = r.HospitalId;
UPDATE g SET PeriodId = o.OwnId FROM [GLPosting] g JOIN #Owned o ON o.SharedId = g.PeriodId AND o.HospitalId = g.HospitalId;
UPDATE d SET PeriodId = o.OwnId FROM [DriverVolume] d JOIN [CostCenter] c ON c.Id = d.CostCenterId JOIN #Owned o ON o.SharedId = d.PeriodId AND o.HospitalId = c.HospitalId;
DELETE p FROM [Period] p WHERE p.HospitalId IS NULL
  AND NOT EXISTS (SELECT 1 FROM [CalculationRun] r WHERE r.PeriodId = p.Id)
  AND NOT EXISTS (SELECT 1 FROM [GLPosting] g WHERE g.PeriodId = p.Id)
  AND NOT EXISTS (SELECT 1 FROM [DriverVolume] d WHERE d.PeriodId = p.Id);
DROP TABLE #Owned;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [ProcedureTypeCosting] DROP CONSTRAINT [CK_ProcedureTypeCosting_Stream];
GO

ALTER TABLE [ProcedureMargin] DROP CONSTRAINT [CK_ProcedureMargin_Stream];
GO

ALTER TABLE [ProcedureTypeCosting] ADD CONSTRAINT [CK_ProcedureTypeCosting_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT'));
GO

ALTER TABLE [ProcedureMargin] ADD CONSTRAINT [CK_ProcedureMargin_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT'));
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Provider] ADD [IsDummy] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

ALTER TABLE [Procedure] ADD [GroupName] nvarchar(200) NULL;
GO

ALTER TABLE [Procedure] ADD [HisCategoryId] nvarchar(200) NULL;
GO

ALTER TABLE [Procedure] ADD [HisGroupId] nvarchar(200) NULL;
GO

ALTER TABLE [Procedure] ADD [HisSubGroupId] nvarchar(200) NULL;
GO

ALTER TABLE [Procedure] ADD [IsPackage] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

ALTER TABLE [Procedure] ADD [SubGroupName] nvarchar(200) NULL;
GO

CREATE TABLE [ProviderClinic] (
    [Id] uniqueidentifier NOT NULL,
    [ProviderId] uniqueidentifier NOT NULL,
    [DepartmentId] uniqueidentifier NOT NULL,
    [HisClinicId] nvarchar(200) NOT NULL,
    [IsDefault] bit NOT NULL,
    [IsActive] bit NOT NULL,
    CONSTRAINT [PK_ProviderClinic] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ProviderClinic_Department_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([Id]),
    CONSTRAINT [FK_ProviderClinic_Provider_ProviderId] FOREIGN KEY ([ProviderId]) REFERENCES [Provider] ([Id])
);
GO

CREATE INDEX [IX_ProviderClinic_DepartmentId] ON [ProviderClinic] ([DepartmentId]);
GO

CREATE UNIQUE INDEX [IX_ProviderClinic_ProviderId_HisClinicId] ON [ProviderClinic] ([ProviderId], [HisClinicId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [ProcedureTypeCosting] ADD [FixedAmount] decimal(19,4) NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [ProcedureTypeCosting] DROP CONSTRAINT [CK_ProcedureTypeCosting_Stream];
GO

ALTER TABLE [ProcedureMargin] DROP CONSTRAINT [CK_ProcedureMargin_Stream];
GO

ALTER TABLE [ProcedureMargin] ADD [FixedAmount] decimal(19,4) NULL;
GO

ALTER TABLE [ProcedureTypeCosting] ADD CONSTRAINT [CK_ProcedureTypeCosting_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT', N'ZERO_FIXED'));
GO

ALTER TABLE [ProcedureMargin] ADD CONSTRAINT [CK_ProcedureMargin_Stream] CHECK ([Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT', N'ZERO_FIXED'));
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [Payer] ADD [HisGroupId] int NULL;
GO

ALTER TABLE [Payer] ADD [HisGroupIsTpa] bit NULL;
GO

ALTER TABLE [Payer] ADD [HisGroupManagedByTpa] bit NULL;
GO

ALTER TABLE [Payer] ADD [HisGroupName] nvarchar(200) NULL;
GO

ALTER TABLE [Insurer] ADD [HisGroupId] int NULL;
GO

ALTER TABLE [Insurer] ADD [IsManagedByTpa] bit NULL;
GO

ALTER TABLE [Insurer] ADD [IsTpa] bit NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RevenueTransaction] ADD [HisInvoiceType] int NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [HisMainInvoiceNo] nvarchar(200) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [IsChildInvoice] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

ALTER TABLE [EncounterService] ADD [IsChildInvoice] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

UPDATE t SET IsChildInvoice = 1 FROM [EncounterService] t
JOIN [Encounter] e ON e.Id = t.EncounterId
JOIN [InvoicePackageItem] i ON i.HospitalId = e.HospitalId
 AND t.SourceRef = CONCAT('INV-', i.TransactionType, '-', i.ChildInvoiceNo, '-', i.ChildInvoiceLineItemNo)
WHERE t.IsChildInvoice = 0 AND i.ChildInvoiceNo IS NOT NULL AND i.ChildInvoiceNo NOT IN ('', '0');
UPDATE t SET IsChildInvoice = 1 FROM [RevenueTransaction] t
JOIN [Encounter] e ON e.Id = t.EncounterId
JOIN [InvoicePackageItem] i ON i.HospitalId = e.HospitalId
 AND t.SourceRef = CONCAT('REV-', i.TransactionType, '-', i.ChildInvoiceNo, '-', i.ChildInvoiceLineItemNo)
WHERE t.IsChildInvoice = 0 AND i.ChildInvoiceNo IS NOT NULL AND i.ChildInvoiceNo NOT IN ('', '0');
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [ProcedureCost] DROP CONSTRAINT [CK_ProcedureCost_CostType];
GO

ALTER TABLE [ProcedureCost] ADD CONSTRAINT [CK_ProcedureCost_CostType] CHECK ([CostType] IN (N'STANDARD', N'ACTUAL', N'BUDGET', N'FORECAST', N'TARGET', N'ZERO_PRICE'));
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RunDimensionCost] ADD [Services] int NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [AssistantThread] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Title] nvarchar(200) NOT NULL,
    [ContextJson] nvarchar(max) NULL,
    [IsPinned] bit NOT NULL,
    [IsArchived] bit NOT NULL,
    [IsDeleted] bit NOT NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [UpdatedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_AssistantThread] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AssistantThread_ContextJson_Json] CHECK ([ContextJson] IS NULL OR ISJSON([ContextJson]) = 1),
    CONSTRAINT [FK_AssistantThread_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [User] ([Id])
);
GO

CREATE TABLE [AssistantMessage] (
    [Id] uniqueidentifier NOT NULL,
    [ThreadId] uniqueidentifier NOT NULL,
    [ParentMessageId] uniqueidentifier NULL,
    [Role] nvarchar(16) NOT NULL,
    [Text] nvarchar(max) NOT NULL,
    [Intent] nvarchar(64) NULL,
    [TurnType] nvarchar(16) NULL,
    [ParamsJson] nvarchar(max) NULL,
    [DetailJson] nvarchar(max) NULL,
    [Permission] nvarchar(200) NULL,
    [RunId] uniqueidentifier NULL,
    [RunCode] nvarchar(200) NULL,
    [Matched] bit NOT NULL,
    [Score] decimal(9,6) NOT NULL,
    [Feedback] int NULL,
    [FeedbackReason] nvarchar(200) NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_AssistantMessage] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AssistantMessage_DetailJson_Json] CHECK ([DetailJson] IS NULL OR ISJSON([DetailJson]) = 1),
    CONSTRAINT [CK_AssistantMessage_ParamsJson_Json] CHECK ([ParamsJson] IS NULL OR ISJSON([ParamsJson]) = 1),
    CONSTRAINT [CK_AssistantMessage_Role] CHECK ([Role] IN (N'user', N'assistant')),
    CONSTRAINT [FK_AssistantMessage_AssistantThread_ThreadId] FOREIGN KEY ([ThreadId]) REFERENCES [AssistantThread] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_AssistantMessage_Matched_CreatedAt] ON [AssistantMessage] ([Matched], [CreatedAt]);
GO

CREATE INDEX [IX_AssistantMessage_ThreadId_CreatedAt] ON [AssistantMessage] ([ThreadId], [CreatedAt]);
GO

CREATE INDEX [IX_AssistantThread_UserId_IsDeleted_UpdatedAt] ON [AssistantThread] ([UserId], [IsDeleted], [UpdatedAt]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [AssistantRoutedPhrase] (
    [Id] uniqueidentifier NOT NULL,
    [TextKey] nvarchar(450) NOT NULL,
    [Intent] nvarchar(64) NOT NULL,
    [Hits] int NOT NULL,
    [CreatedAt] datetime2(3) NOT NULL,
    [LastUsedAt] datetime2(3) NOT NULL,
    CONSTRAINT [PK_AssistantRoutedPhrase] PRIMARY KEY ([Id])
);
GO

CREATE UNIQUE INDEX [IX_AssistantRoutedPhrase_TextKey] ON [AssistantRoutedPhrase] ([TextKey]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_Procedure_Code] ON [Procedure];
GO

DROP INDEX [IX_Procedure_HospitalId] ON [Procedure];
GO

DROP INDEX [IX_Package_Code] ON [Package];
GO

DROP INDEX [IX_Package_HospitalId] ON [Package];
GO


                UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
                FROM [Procedure] p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;
                UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
                FROM Package p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;
GO

CREATE UNIQUE INDEX [IX_Procedure_HospitalId_Code] ON [Procedure] ([HospitalId], [Code]);
GO

CREATE UNIQUE INDEX [IX_Package_HospitalId_Code] ON [Package] ([HospitalId], [Code]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE INDEX [IX_RevenueTransaction_PostedAt] ON [RevenueTransaction] ([PostedAt]) INCLUDE ([EncounterId], [ProcedureId], [SourceRef], [HisLineStatus], [HisInvoiceType], [NetAmount], [RefundedAmount], [HisUnitPrice], [IsChildInvoice]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [RunHisInvoicing] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [InvoiceType] int NOT NULL,
    [Invoices] int NOT NULL,
    [Encounters] int NOT NULL,
    [Patients] int NOT NULL,
    [Refunded] decimal(19,4) NOT NULL,
    [Revenue] decimal(19,4) NOT NULL,
    [MinInvoiceNo] bigint NULL,
    [MaxInvoiceNo] bigint NULL,
    CONSTRAINT [PK_RunHisInvoicing] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RunHisInvoicing_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE UNIQUE INDEX [IX_RunHisInvoicing_RunId_InvoiceType] ON [RunHisInvoicing] ([RunId], [InvoiceType]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ImportFile] (
    [Id] uniqueidentifier NOT NULL,
    [FileName] nvarchar(200) NOT NULL,
    [SizeBytes] bigint NOT NULL,
    [Sha256] nvarchar(64) NOT NULL,
    [UploadedAt] datetime2(3) NOT NULL,
    [UploadedBy] nvarchar(200) NOT NULL,
    [IsHisPack] bit NOT NULL,
    [HisSetupId] nvarchar(200) NULL,
    [HisProjectId] int NULL,
    [Pack] nvarchar(200) NULL,
    [FactsFrom] nvarchar(200) NULL,
    [FactsTo] nvarchar(200) NULL,
    [Hospital] nvarchar(200) NULL,
    [TotalRows] int NOT NULL,
    [ContextJson] nvarchar(max) NULL,
    [Status] nvarchar(32) NOT NULL,
    [LastDryRun] bit NOT NULL,
    [TablesTotal] int NOT NULL,
    [TablesDone] int NOT NULL,
    [CurrentTable] nvarchar(200) NULL,
    [Attempts] int NOT NULL,
    [QueuedAt] datetime2(3) NULL,
    [StartedAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [RunBy] nvarchar(200) NULL,
    [RowsCreated] int NOT NULL,
    [RowsUpdated] int NOT NULL,
    [RowsUnchanged] int NOT NULL,
    [RowsRejected] int NOT NULL,
    [DetailJson] nvarchar(max) NULL,
    [ErrorMessage] nvarchar(max) NULL,
    CONSTRAINT [PK_ImportFile] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ImportFile_ContextJson_Json] CHECK ([ContextJson] IS NULL OR ISJSON([ContextJson]) = 1),
    CONSTRAINT [CK_ImportFile_DetailJson_Json] CHECK ([DetailJson] IS NULL OR ISJSON([DetailJson]) = 1)
);
GO

CREATE TABLE [ImportFileContent] (
    [Id] uniqueidentifier NOT NULL,
    [Content] varbinary(max) NOT NULL,
    CONSTRAINT [PK_ImportFileContent] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ImportFileContent_ImportFile_Id] FOREIGN KEY ([Id]) REFERENCES [ImportFile] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_ImportFile_UploadedAt] ON [ImportFile] ([UploadedAt]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ConsumptionItem] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(200) NOT NULL,
    [ServiceRef] nvarchar(200) NULL,
    [LinkMethod] nvarchar(200) NOT NULL,
    [OtReservationId] int NULL,
    [Category] nvarchar(200) NOT NULL,
    [ItemKey] nvarchar(200) NOT NULL,
    [ItemKeySystem] nvarchar(200) NOT NULL,
    [OracleItemCode] nvarchar(200) NULL,
    [Barcode] nvarchar(200) NULL,
    [Description] nvarchar(max) NULL,
    [Quantity] decimal(19,6) NOT NULL,
    [Uom] nvarchar(200) NOT NULL,
    [ConversionFactor] decimal(19,6) NULL,
    [PrescribedQty] decimal(19,6) NULL,
    [CostCentreCode] nvarchar(200) NULL,
    [LocationId] nvarchar(200) NULL,
    [BilledAmount] decimal(19,4) NULL,
    [HisUnitPrice] decimal(19,6) NULL,
    [OccurredAt] datetime2(3) NOT NULL,
    [SourceTable] nvarchar(200) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(120) NOT NULL,
    CONSTRAINT [PK_ConsumptionItem] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ConsumptionItem_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_ConsumptionItem_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE TABLE [StaffTime] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(200) NOT NULL,
    [ServiceRef] nvarchar(200) NULL,
    [LinkMethod] nvarchar(200) NOT NULL,
    [OtReservationId] int NULL,
    [StaffRole] nvarchar(200) NOT NULL,
    [StaffType] nvarchar(200) NULL,
    [StaffId] nvarchar(200) NULL,
    [ProviderId] uniqueidentifier NULL,
    [Minutes] decimal(19,6) NOT NULL,
    [Basis] nvarchar(200) NOT NULL,
    [Volume] decimal(19,6) NULL,
    [VolumeUom] nvarchar(200) NULL,
    [CostCentreCode] nvarchar(200) NULL,
    [OccurredAt] datetime2(3) NOT NULL,
    [SourceTable] nvarchar(200) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(120) NOT NULL,
    CONSTRAINT [PK_StaffTime] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_StaffTime_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_StaffTime_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_StaffTime_Provider_ProviderId] FOREIGN KEY ([ProviderId]) REFERENCES [Provider] ([Id])
);
GO

CREATE INDEX [IX_ConsumptionItem_EncounterId] ON [ConsumptionItem] ([EncounterId]);
GO

CREATE INDEX [IX_ConsumptionItem_HospitalId_HisEncounterNo] ON [ConsumptionItem] ([HospitalId], [HisEncounterNo]);
GO

CREATE UNIQUE INDEX [IX_ConsumptionItem_HospitalId_SourceRef] ON [ConsumptionItem] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_StaffTime_EncounterId] ON [StaffTime] ([EncounterId]);
GO

CREATE INDEX [IX_StaffTime_HospitalId_HisEncounterNo] ON [StaffTime] ([HospitalId], [HisEncounterNo]);
GO

CREATE UNIQUE INDEX [IX_StaffTime_HospitalId_SourceRef] ON [StaffTime] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_StaffTime_ProviderId] ON [StaffTime] ([ProviderId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RevenueTransaction] ADD [DoctorShare] decimal(19,6) NULL;
GO

ALTER TABLE [RevenueTransaction] ADD [DoctorShareType] nvarchar(1) NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [EncounterTransfer] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(200) NOT NULL,
    [TransferNo] int NOT NULL,
    [TransferDate] datetime2(3) NOT NULL,
    [StartTime] nvarchar(50) NULL,
    [EndTime] nvarchar(50) NULL,
    [Duration] int NULL,
    [HisClinicId] int NULL,
    [CostCentreCode] nvarchar(200) NULL,
    [SourceTable] nvarchar(200) NOT NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(120) NOT NULL,
    CONSTRAINT [PK_EncounterTransfer] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_EncounterTransfer_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_EncounterTransfer_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE INDEX [IX_EncounterTransfer_EncounterId] ON [EncounterTransfer] ([EncounterId]);
GO

CREATE INDEX [IX_EncounterTransfer_HospitalId_HisEncounterNo] ON [EncounterTransfer] ([HospitalId], [HisEncounterNo]);
GO

CREATE UNIQUE INDEX [IX_EncounterTransfer_HospitalId_SourceRef] ON [EncounterTransfer] ([HospitalId], [SourceRef]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [StaffTime] ADD [ClinicId] int NULL;
GO

ALTER TABLE [StaffTime] ADD [EndTime] nvarchar(50) NULL;
GO

ALTER TABLE [StaffTime] ADD [OtId] int NULL;
GO

ALTER TABLE [StaffTime] ADD [StartTime] nvarchar(50) NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [BedId] nvarchar(20) NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [CategoryId] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [Charges] decimal(19,6) NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [DoctorId] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [NursingStationId] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [PatientStatus] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [PerformingClinicId] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [PerformingDoctorId] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [Remarks] nvarchar(500) NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [RoomId] nvarchar(20) NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [Status] int NULL;
GO

ALTER TABLE [EncounterTransfer] ADD [TransferType] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [BillingPrice] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [HisGrossAmount] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [HisSource] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [HisTransType] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [ItemCategoryId] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [MarkUp] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [NursingStationId] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [OrderLineItemNo] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [OrderNo] int NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [RefundableQty] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [SellingPrice] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [StockQtySnapshot] decimal(19,6) NULL;
GO

ALTER TABLE [ConsumptionItem] ADD [SubCategoryId] int NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ClaimLine] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(50) NULL,
    [PatientMrn] nvarchar(200) NULL,
    [PayerId] uniqueidentifier NULL,
    [RevenueRef] nvarchar(100) NULL,
    [ClaimNo] int NULL,
    [ClaimDate] datetime2(3) NULL,
    [InvoiceTransactionType] int NULL,
    [InvoiceNo] int NULL,
    [MainInvoiceNo] int NULL,
    [LineItemNo] int NULL,
    [HisPatientType] int NULL,
    [HisProcedureId] nvarchar(200) NULL,
    [ClaimAmount] decimal(19,4) NULL,
    [InvoiceAmount] decimal(19,4) NULL,
    [SettlementAmount] decimal(19,4) NULL,
    [RejectedAmount] decimal(19,4) NULL,
    [TaxAmount] decimal(19,4) NULL,
    [PrimaryDoctorId] int NULL,
    [PrimaryDoctorShare] decimal(19,4) NULL,
    [PrimaryDoctorShareType] nvarchar(200) NULL,
    [ReferralClinicId] int NULL,
    [ReferralDoctorId] int NULL,
    [ReferralDoctorShare] decimal(19,4) NULL,
    [ReferralDoctorShareType] nvarchar(200) NULL,
    [Remarks] nvarchar(200) NULL,
    [EClaimStatus] int NULL,
    [EClaimRejectionReason] int NULL,
    [EClaimRemarks] nvarchar(200) NULL,
    [LineCreatedOn] datetime2(3) NULL,
    [LineEditedOn] datetime2(3) NULL,
    [InvInvoiceAmount] decimal(19,4) NULL,
    [InvClaimAmount] decimal(19,4) NULL,
    [InvSettlementAmount] decimal(19,4) NULL,
    [InvTaxAmount] decimal(19,4) NULL,
    [InvRemarks] nvarchar(200) NULL,
    [InvIsPackageInvoice] bit NULL,
    [InvIsActive] bit NULL,
    [InvEClaimStatus] int NULL,
    [InvEClaimRefNum] nvarchar(200) NULL,
    [InvEClaimRemarks] nvarchar(200) NULL,
    [NphiesClaimStatus] int NULL,
    [NphiesClaimRefNum] nvarchar(200) NULL,
    [NphiesClaimRemarks] nvarchar(200) NULL,
    [NphiesTaxAmount] decimal(19,4) NULL,
    [NphiesBatchIdentifier] nvarchar(200) NULL,
    [NphiesBatchNumber] int NULL,
    [NphiesClaimRemarks2] nvarchar(200) NULL,
    [NphiesSubmittedOn] datetime2(3) NULL,
    [NphiesResponseReceivedOn] datetime2(3) NULL,
    [NphiesAmountSubmitted] decimal(19,4) NULL,
    [NphiesAmountApproved] decimal(19,4) NULL,
    [ClaimBundleId] nvarchar(200) NULL,
    [ClaimBatchBundleId] nvarchar(200) NULL,
    [IsPendedReceived] bit NULL,
    [ReconciliationBundleId] nvarchar(200) NULL,
    [ResubmissionStatus] int NULL,
    [AttachmentStatus] int NULL,
    [InvCreatedOn] datetime2(3) NULL,
    [ClaimCompanyId] int NULL,
    [ClaimInvoiceFromDate] datetime2(3) NULL,
    [ClaimInvoiceToDate] datetime2(3) NULL,
    [ClaimInvoiceAmount] decimal(19,4) NULL,
    [ClaimTotalAmount] decimal(19,4) NULL,
    [ClaimSettledAmount] decimal(19,4) NULL,
    [ClaimNoOfSettlement] int NULL,
    [ClaimLastSettlementNo] int NULL,
    [ClaimLastSettlementDate] datetime2(3) NULL,
    [ClaimIsPosted2Gl] bit NULL,
    [ClaimIsTransactionClosed] bit NULL,
    [ClaimStatus] int NULL,
    [ClaimIsEClaimed] bit NULL,
    [ClaimEClaimStatus] int NULL,
    [ClaimInvoiceTypeId] int NULL,
    [ClaimTaxAmount] decimal(19,4) NULL,
    [ClaimUpdateDate] datetime2(3) NULL,
    [ClaimNphiesStatus] int NULL,
    [ClaimNphiesUpdateDate] datetime2(3) NULL,
    [ClaimNphiesReference] nvarchar(200) NULL,
    [ClaimEClaimSubmittedOn] datetime2(3) NULL,
    [ClaimNphiesSubmittedOn] datetime2(3) NULL,
    [ClaimNphiesReSubmittedOn] datetime2(3) NULL,
    [ClaimIsPostedToZatca] bit NULL,
    [ClaimOracleBatchNo] nvarchar(200) NULL,
    [ClaimCreatedOn] datetime2(3) NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(150) NOT NULL,
    CONSTRAINT [PK_ClaimLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ClaimLine_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_ClaimLine_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_ClaimLine_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id])
);
GO

CREATE TABLE [NphiesClaimLine] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(50) NULL,
    [PatientMrn] nvarchar(200) NULL,
    [PayerId] uniqueidentifier NULL,
    [ClaimType] int NULL,
    [InvoiceTransactionType] int NULL,
    [InvoiceNo] int NULL,
    [ClaimIdentifier] nvarchar(50) NULL,
    [SequenceNo] int NULL,
    [HisProcedureId] nvarchar(200) NULL,
    [AmountSubmitted] decimal(19,4) NULL,
    [AmountApproved] decimal(19,4) NULL,
    [IsApproved] bit NULL,
    [Remarks] nvarchar(200) NULL,
    [PatientShare] decimal(19,4) NULL,
    [CompanyTax] decimal(19,4) NULL,
    [NphiesStatus] int NULL,
    [SubmittedQuantity] nvarchar(200) NULL,
    [ApprovedQuantity] nvarchar(200) NULL,
    [ReasonCode] nvarchar(200) NULL,
    [TaxApproved] nvarchar(200) NULL,
    [ClaimStatus] int NULL,
    [ClaimBundleId] nvarchar(200) NULL,
    [LineCreatedOn] datetime2(3) NULL,
    [LineEditedOn] datetime2(3) NULL,
    [RejectionRemarks] nvarchar(200) NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(150) NOT NULL,
    CONSTRAINT [PK_NphiesClaimLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_NphiesClaimLine_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_NphiesClaimLine_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_NphiesClaimLine_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id])
);
GO

CREATE TABLE [NphiesPayment] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [PaymentReconIdentifier] nvarchar(200) NULL,
    [LineItemNo] int NULL,
    [ComponentPayment] nvarchar(200) NULL,
    [ComponentEarlyFee] nvarchar(200) NULL,
    [ComponentNphiesFee] nvarchar(200) NULL,
    [Identifier] nvarchar(200) NULL,
    [Predecessor] nvarchar(200) NULL,
    [Type] nvarchar(200) NULL,
    [ClaimRequestIdentifier] nvarchar(50) NULL,
    [ClaimRequestSystem] nvarchar(200) NULL,
    [ClaimResponseIdentifier] nvarchar(200) NULL,
    [ClaimResponseSystem] nvarchar(200) NULL,
    [PayeeOrg] nvarchar(200) NULL,
    [SubmitterOrg] nvarchar(200) NULL,
    [Amount] nvarchar(200) NULL,
    [AmountValue] decimal(19,4) NULL,
    [ComponentPaymentValue] decimal(19,4) NULL,
    [Date] datetime2(3) NULL,
    [LineCreatedOn] datetime2(3) NULL,
    [IdentifierSystem] nvarchar(200) NULL,
    [PaymentIssuer] nvarchar(200) NULL,
    [Status] nvarchar(200) NULL,
    [StartDate] datetime2(3) NULL,
    [EndDate] datetime2(3) NULL,
    [PaymentCreatedOn] datetime2(3) NULL,
    [Outcome] nvarchar(200) NULL,
    [Disposition] nvarchar(200) NULL,
    [PaymentDate] datetime2(3) NULL,
    [PaymentAmount] nvarchar(200) NULL,
    [PaymentAmountValue] decimal(19,4) NULL,
    [PaymentIdentifier] nvarchar(200) NULL,
    [PaymentIdentifierSystem] nvarchar(200) NULL,
    [IsPaymentNoticeSent] bit NULL,
    [PaymentNoticeSentOn] datetime2(3) NULL,
    [CreatedOn] datetime2(3) NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(150) NOT NULL,
    CONSTRAINT [PK_NphiesPayment] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_NphiesPayment_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE TABLE [SettlementLine] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NULL,
    [HisEncounterNo] nvarchar(50) NULL,
    [PatientMrn] nvarchar(200) NULL,
    [PayerId] uniqueidentifier NULL,
    [RevenueRef] nvarchar(100) NULL,
    [SettlementTransactionType] int NULL,
    [SettlementNo] int NULL,
    [SettlementDate] datetime2(3) NULL,
    [ClaimNo] int NULL,
    [InvoiceTransactionType] int NULL,
    [InvoiceNo] int NULL,
    [MainInvoiceNo] int NULL,
    [LineItemNo] int NULL,
    [HisProcedureId] nvarchar(200) NULL,
    [ClaimedAmount] decimal(19,4) NULL,
    [PaidAmount] decimal(19,4) NULL,
    [RejectedAmount] decimal(19,4) NULL,
    [RejectionReasonId] int NULL,
    [PrimaryDoctorId] int NULL,
    [PrimaryDoctorShare] decimal(19,4) NULL,
    [PrimaryDoctorShareType] nvarchar(200) NULL,
    [ReferralClinicId] int NULL,
    [ReferralDoctorId] int NULL,
    [ReferralDoctorShare] decimal(19,4) NULL,
    [ReferralDoctorShareType] nvarchar(200) NULL,
    [PartnerId] int NULL,
    [PartnerShare] decimal(19,4) NULL,
    [PartnerShareType] nvarchar(200) NULL,
    [Remarks] nvarchar(200) NULL,
    [LineCreatedOn] datetime2(3) NULL,
    [LineEditedOn] datetime2(3) NULL,
    [InvInvoiceAmount] decimal(19,4) NULL,
    [InvClaimedAmount] decimal(19,4) NULL,
    [InvSettledAmount] decimal(19,4) NULL,
    [InvRejectedAmount] decimal(19,4) NULL,
    [InvTaxAmount] decimal(19,4) NULL,
    [InvRemarks] nvarchar(200) NULL,
    [InvIsActive] bit NULL,
    [InvIsPackageInvoice] bit NULL,
    [SettlementCompanyId] int NULL,
    [OfficialReceiptNo] nvarchar(200) NULL,
    [OfficialReceiptDate] datetime2(3) NULL,
    [OfficialReceiptReference] nvarchar(200) NULL,
    [ReceiptAmount] decimal(19,4) NULL,
    [IsIncomeShared] bit NULL,
    [IncShareProcessedOn] datetime2(3) NULL,
    [IsPosted2Gl] bit NULL,
    [IsTransactionClosed] bit NULL,
    [SettlementStatus] int NULL,
    [SettlementTaxAmount] decimal(19,4) NULL,
    [ErpInterfaceId] nvarchar(200) NULL,
    [SettlementCreatedOn] datetime2(3) NULL,
    [SourceSystem] nvarchar(200) NOT NULL,
    [SourceRef] nvarchar(150) NOT NULL,
    CONSTRAINT [PK_SettlementLine] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_SettlementLine_Encounter_EncounterId] FOREIGN KEY ([EncounterId]) REFERENCES [Encounter] ([Id]),
    CONSTRAINT [FK_SettlementLine_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]),
    CONSTRAINT [FK_SettlementLine_Payer_PayerId] FOREIGN KEY ([PayerId]) REFERENCES [Payer] ([Id])
);
GO

CREATE INDEX [IX_ClaimLine_EncounterId] ON [ClaimLine] ([EncounterId]);
GO

CREATE INDEX [IX_ClaimLine_HospitalId_HisEncounterNo] ON [ClaimLine] ([HospitalId], [HisEncounterNo]);
GO

CREATE INDEX [IX_ClaimLine_HospitalId_RevenueRef] ON [ClaimLine] ([HospitalId], [RevenueRef]);
GO

CREATE UNIQUE INDEX [IX_ClaimLine_HospitalId_SourceRef] ON [ClaimLine] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_ClaimLine_PayerId] ON [ClaimLine] ([PayerId]);
GO

CREATE INDEX [IX_NphiesClaimLine_EncounterId] ON [NphiesClaimLine] ([EncounterId]);
GO

CREATE INDEX [IX_NphiesClaimLine_HospitalId_ClaimIdentifier] ON [NphiesClaimLine] ([HospitalId], [ClaimIdentifier]);
GO

CREATE INDEX [IX_NphiesClaimLine_HospitalId_HisEncounterNo] ON [NphiesClaimLine] ([HospitalId], [HisEncounterNo]);
GO

CREATE UNIQUE INDEX [IX_NphiesClaimLine_HospitalId_SourceRef] ON [NphiesClaimLine] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_NphiesClaimLine_PayerId] ON [NphiesClaimLine] ([PayerId]);
GO

CREATE INDEX [IX_NphiesPayment_HospitalId_ClaimRequestIdentifier] ON [NphiesPayment] ([HospitalId], [ClaimRequestIdentifier]);
GO

CREATE UNIQUE INDEX [IX_NphiesPayment_HospitalId_SourceRef] ON [NphiesPayment] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_SettlementLine_EncounterId] ON [SettlementLine] ([EncounterId]);
GO

CREATE INDEX [IX_SettlementLine_HospitalId_HisEncounterNo] ON [SettlementLine] ([HospitalId], [HisEncounterNo]);
GO

CREATE INDEX [IX_SettlementLine_HospitalId_RevenueRef] ON [SettlementLine] ([HospitalId], [RevenueRef]);
GO

CREATE UNIQUE INDEX [IX_SettlementLine_HospitalId_SourceRef] ON [SettlementLine] ([HospitalId], [SourceRef]);
GO

CREATE INDEX [IX_SettlementLine_PayerId] ON [SettlementLine] ([PayerId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [RunPackageBilling] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [PackageId] uniqueidentifier NOT NULL,
    [Encounters] int NOT NULL,
    [DoctorShare] decimal(19,4) NULL,
    [FeeBilled] decimal(19,4) NOT NULL,
    [MaterialsBilled] decimal(19,4) NOT NULL,
    [MaterialItems] int NOT NULL,
    [MedicineBilled] decimal(19,4) NOT NULL,
    [MedicineQuantity] decimal(19,6) NOT NULL,
    [WardDays] int NOT NULL,
    [NurseMinutes] decimal(19,6) NOT NULL,
    [Claimed] decimal(19,4) NOT NULL,
    [Settled] decimal(19,4) NOT NULL,
    [Rejected] decimal(19,4) NOT NULL,
    CONSTRAINT [PK_RunPackageBilling] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RunPackageBilling_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE UNIQUE INDEX [IX_RunPackageBilling_RunId_PackageId] ON [RunPackageBilling] ([RunId], [PackageId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DECLARE @var4 sysname;
SELECT @var4 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[RunPackageBilling]') AND [c].[name] = N'WardDays');
IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [RunPackageBilling] DROP CONSTRAINT [' + @var4 + '];');
ALTER TABLE [RunPackageBilling] ALTER COLUMN [WardDays] decimal(19,6) NOT NULL;
GO

ALTER TABLE [RunPackageBilling] ADD [StaffMinutes] decimal(19,6) NOT NULL DEFAULT 0.0;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RunPackageBilling] ADD [Cases] int NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [MedicinePrice] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [MedicineCode] nvarchar(200) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [GenericName] nvarchar(200) NULL,
    [Strength] nvarchar(200) NULL,
    [PackQuantity] decimal(19,6) NULL,
    [QuantityUnit] int NULL,
    [StdSellingPrice] decimal(19,6) NULL,
    [StdPurchasePrice] decimal(19,6) NULL,
    [Vat] decimal(19,6) NULL,
    [HisStatus] int NULL,
    [Source] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_MedicinePrice] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_MedicinePrice_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id])
);
GO

CREATE UNIQUE INDEX [IX_MedicinePrice_HospitalId_MedicineCode] ON [MedicinePrice] ([HospitalId], [MedicineCode]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RevenueTransaction] ADD [HisIsClaimed] bit NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [RunPackageDoctor] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [PackageId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [ProviderId] uniqueidentifier NULL,
    [Performed] int NOT NULL,
    [FeeBilled] decimal(19,4) NOT NULL,
    [DoctorShare] decimal(19,4) NULL,
    [ConsumablesBilled] decimal(19,4) NOT NULL,
    [ConsumablesCovered] decimal(19,4) NOT NULL,
    [ConsumablesClaimed] decimal(19,4) NOT NULL,
    [ConsumableItems] decimal(19,6) NOT NULL,
    [MedicineBilled] decimal(19,4) NOT NULL,
    [MedicineCovered] decimal(19,4) NOT NULL,
    [MedicineCoveredQty] decimal(19,6) NOT NULL,
    [MedicineLines] decimal(19,6) NOT NULL,
    [MedicineClaimed] decimal(19,4) NOT NULL,
    [Claimed] decimal(19,4) NOT NULL,
    [ClaimsReceived] decimal(19,4) NULL,
    [Settled] decimal(19,4) NOT NULL,
    [Rejected] decimal(19,4) NOT NULL,
    [WardDays] decimal(19,6) NOT NULL,
    [StaffMinutes] decimal(19,6) NOT NULL,
    CONSTRAINT [PK_RunPackageDoctor] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RunPackageDoctor_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_RunPackageDoctor_RunId_PackageId] ON [RunPackageDoctor] ([RunId], [PackageId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [RunProcedureDoctor] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [ProcedureId] uniqueidentifier NOT NULL,
    [EncounterId] uniqueidentifier NOT NULL,
    [ProviderId] uniqueidentifier NULL,
    [Performed] decimal(19,6) NOT NULL,
    [FeeBilled] decimal(19,4) NOT NULL,
    [Consumables] decimal(19,4) NOT NULL,
    [Medicine] decimal(19,4) NOT NULL,
    [Claimed] decimal(19,4) NOT NULL,
    [ClaimsReceived] decimal(19,4) NULL,
    [Settled] decimal(19,4) NOT NULL,
    [Rejected] decimal(19,4) NOT NULL,
    CONSTRAINT [PK_RunProcedureDoctor] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RunProcedureDoctor_CalculationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [CalculationRun] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_RunPackageDoctor_RunId_ProviderId] ON [RunPackageDoctor] ([RunId], [ProviderId]);
GO

CREATE INDEX [IX_RunProcedureDoctor_RunId_ProviderId] ON [RunProcedureDoctor] ([RunId], [ProviderId]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [RunPackageDoctor] ADD [ClaimsReceivedLines] decimal(19,4) NULL;
GO

ALTER TABLE [RunPackageBilling] ADD [ClaimsReceived] decimal(19,4) NULL;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [HisSyncLog] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [At] datetime2(3) NOT NULL,
    [Kind] nvarchar(16) NOT NULL,
    [Level] nvarchar(8) NOT NULL,
    [Message] nvarchar(max) NOT NULL,
    [Detail] nvarchar(200) NULL,
    [FactDate] date NULL,
    [Actor] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_HisSyncLog] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncLog_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [HisSyncRange] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [FromDate] date NOT NULL,
    [ToDate] date NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [DaysTotal] int NOT NULL,
    [DaysDone] int NOT NULL,
    [RequestedAt] datetime2(3) NOT NULL,
    [RequestedBy] nvarchar(256) NOT NULL,
    [StartedAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [Error] nvarchar(200) NULL,
    CONSTRAINT [PK_HisSyncRange] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncRange_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [HisSyncSetting] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [CatalogueEnabled] bit NOT NULL,
    [CatalogueTime] time NOT NULL,
    [CatalogueRequestedAt] datetime2(3) NULL,
    [CatalogueRequestedBy] nvarchar(256) NULL,
    [CatalogueLastRunAt] datetime2(3) NULL,
    [UpdatedAt] datetime2(3) NOT NULL,
    [UpdatedBy] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_HisSyncSetting] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncSetting_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [HisSyncWorker] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [Kind] nvarchar(16) NOT NULL,
    [Status] nvarchar(16) NOT NULL,
    [Step] nvarchar(200) NULL,
    [RangeId] uniqueidentifier NULL,
    [CurrentDate] date NULL,
    [BatchesDone] int NOT NULL,
    [BatchesTotal] int NULL,
    [RecordsDone] int NOT NULL,
    [RecordsTotal] int NULL,
    [StartedAt] datetime2(3) NULL,
    [HeartbeatAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [LastError] nvarchar(200) NULL,
    CONSTRAINT [PK_HisSyncWorker] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncWorker_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_HisSyncLog_HospitalId_At] ON [HisSyncLog] ([HospitalId], [At]);
GO

CREATE INDEX [IX_HisSyncRange_HospitalId_FromDate] ON [HisSyncRange] ([HospitalId], [FromDate]);
GO

CREATE UNIQUE INDEX [IX_HisSyncSetting_HospitalId] ON [HisSyncSetting] ([HospitalId]);
GO

CREATE UNIQUE INDEX [IX_HisSyncWorker_HospitalId_Kind] ON [HisSyncWorker] ([HospitalId], [Kind]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [HisSyncCatalogueChange] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [ImportFileId] uniqueidentifier NULL,
    [DetectedAt] datetime2(3) NOT NULL,
    [Status] nvarchar(16) NOT NULL,
    [Created] int NOT NULL,
    [Updated] int NOT NULL,
    [Unchanged] int NOT NULL,
    [Rejected] int NOT NULL,
    [TablesJson] nvarchar(200) NOT NULL,
    [DecidedAt] datetime2(3) NULL,
    [DecidedBy] nvarchar(256) NULL,
    CONSTRAINT [PK_HisSyncCatalogueChange] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncCatalogueChange_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [HisSyncDay] (
    [Id] uniqueidentifier NOT NULL,
    [HospitalId] uniqueidentifier NOT NULL,
    [RangeId] uniqueidentifier NOT NULL,
    [Date] date NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [Encounters] int NOT NULL,
    [Batches] int NOT NULL,
    [Synced] int NOT NULL,
    [Failed] int NOT NULL,
    [Attempts] int NOT NULL,
    [RefundsDone] bit NOT NULL,
    [StartedAt] datetime2(3) NULL,
    [FinishedAt] datetime2(3) NULL,
    [Error] nvarchar(200) NULL,
    CONSTRAINT [PK_HisSyncDay] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncDay_HisSyncRange_RangeId] FOREIGN KEY ([RangeId]) REFERENCES [HisSyncRange] ([Id]),
    CONSTRAINT [FK_HisSyncDay_Hospital_HospitalId] FOREIGN KEY ([HospitalId]) REFERENCES [Hospital] ([Id]) ON DELETE CASCADE
);
GO

CREATE TABLE [HisSyncEncounter] (
    [Id] uniqueidentifier NOT NULL,
    [DayId] uniqueidentifier NOT NULL,
    [Type] nvarchar(2) NOT NULL,
    [HisNo] int NOT NULL,
    [GroupNo] int NOT NULL,
    [BatchNo] int NOT NULL,
    [Status] nvarchar(16) NOT NULL,
    [Attempts] int NOT NULL,
    [Error] nvarchar(2000) NULL,
    [SyncedAt] datetime2(3) NULL,
    [LinkAppointmentId] int NULL,
    [LinkInvoiceAppointment] int NULL,
    [LinkInvoiceCompany] int NULL,
    CONSTRAINT [PK_HisSyncEncounter] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_HisSyncEncounter_HisSyncDay_DayId] FOREIGN KEY ([DayId]) REFERENCES [HisSyncDay] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_HisSyncCatalogueChange_HospitalId_DetectedAt] ON [HisSyncCatalogueChange] ([HospitalId], [DetectedAt]);
GO

CREATE UNIQUE INDEX [IX_HisSyncDay_HospitalId_Date] ON [HisSyncDay] ([HospitalId], [Date]);
GO

CREATE INDEX [IX_HisSyncDay_RangeId] ON [HisSyncDay] ([RangeId]);
GO

CREATE INDEX [IX_HisSyncEncounter_DayId_Status_BatchNo] ON [HisSyncEncounter] ([DayId], [Status], [BatchNo]);
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DECLARE @var5 sysname;
SELECT @var5 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncWorker]') AND [c].[name] = N'Step');
IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncWorker] DROP CONSTRAINT [' + @var5 + '];');
ALTER TABLE [HisSyncWorker] ALTER COLUMN [Step] nvarchar(400) NULL;
GO

DECLARE @var6 sysname;
SELECT @var6 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncWorker]') AND [c].[name] = N'LastError');
IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncWorker] DROP CONSTRAINT [' + @var6 + '];');
ALTER TABLE [HisSyncWorker] ALTER COLUMN [LastError] nvarchar(2000) NULL;
GO

DECLARE @var7 sysname;
SELECT @var7 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncRange]') AND [c].[name] = N'Error');
IF @var7 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncRange] DROP CONSTRAINT [' + @var7 + '];');
ALTER TABLE [HisSyncRange] ALTER COLUMN [Error] nvarchar(2000) NULL;
GO

DECLARE @var8 sysname;
SELECT @var8 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncLog]') AND [c].[name] = N'Detail');
IF @var8 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncLog] DROP CONSTRAINT [' + @var8 + '];');
ALTER TABLE [HisSyncLog] ALTER COLUMN [Detail] nvarchar(4000) NULL;
GO

DECLARE @var9 sysname;
SELECT @var9 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncDay]') AND [c].[name] = N'Error');
IF @var9 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncDay] DROP CONSTRAINT [' + @var9 + '];');
ALTER TABLE [HisSyncDay] ALTER COLUMN [Error] nvarchar(2000) NULL;
GO

DECLARE @var10 sysname;
SELECT @var10 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[HisSyncCatalogueChange]') AND [c].[name] = N'TablesJson');
IF @var10 IS NOT NULL EXEC(N'ALTER TABLE [HisSyncCatalogueChange] DROP CONSTRAINT [' + @var10 + '];');
ALTER TABLE [HisSyncCatalogueChange] ALTER COLUMN [TablesJson] nvarchar(max) NOT NULL;
GO

COMMIT;
GO


SET NOEXEC OFF;
GO
