using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Verita.Infrastructure.Data.Migrations;

/// <summary>
/// Database rules the EF model cannot express, applied after the generated schema:
/// the non-overlap guard on resource rates (CR-9) and the published-only views the warehouse
/// and BI read (OUT-1, OUT-2).
/// </summary>
[DbContext(typeof(VeritaDbContext))]
[Migration("20261006130000_DatabaseGuards")]
public sealed class DatabaseGuards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // CR-9: exactly one rate may be effective for a resource on any date. Business effective
        // dating is enforced in the database; DQ-RATE-OVERLAP is the backstop, not the only guard.
        migrationBuilder.Sql("""
            CREATE FUNCTION dbo.fn_ResourceRateOverlaps(@ResourceId UNIQUEIDENTIFIER, @Id UNIQUEIDENTIFIER, @From DATE, @To DATE)
            RETURNS INT WITH SCHEMABINDING AS
            BEGIN
                RETURN (SELECT COUNT(*) FROM dbo.ResourceRate r
                        WHERE r.ResourceId = @ResourceId AND r.Id <> @Id
                          AND r.EffectiveFrom <= ISNULL(@To, '9999-12-31')
                          AND ISNULL(r.EffectiveTo, '9999-12-31') >= @From);
            END
            """);
        migrationBuilder.Sql("""
            ALTER TABLE dbo.ResourceRate WITH CHECK ADD CONSTRAINT CK_ResourceRate_NoOverlap
                CHECK (dbo.fn_ResourceRateOverlaps(ResourceId, Id, EffectiveFrom, EffectiveTo) = 0);
            """);

        // OUT-1 / OUT-2: the warehouse and BI see published runs only — never a draft (BRD 22.3).
        migrationBuilder.Sql("""
            CREATE VIEW dbo.vw_PublishedEncounterCost AS
            SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, v.Code AS CostingVersion, ec.*
            FROM dbo.EncounterCost ec
            JOIN dbo.CalculationRun r ON r.Id = ec.RunId AND r.Status = 'PUBLISHED'
            JOIN dbo.Hospital h ON h.Id = r.HospitalId
            JOIN dbo.Period p ON p.Id = r.PeriodId
            JOIN dbo.CostingVersion v ON v.Id = r.CostingVersionId
            """);
        migrationBuilder.Sql("""
            CREATE VIEW dbo.vw_PublishedCostComponent AS
            SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, cc.*
            FROM dbo.CostComponent cc
            JOIN dbo.CalculationRun r ON r.Id = cc.RunId AND r.Status = 'PUBLISHED'
            JOIN dbo.Hospital h ON h.Id = r.HospitalId
            JOIN dbo.Period p ON p.Id = r.PeriodId
            """);
        migrationBuilder.Sql("""
            CREATE VIEW dbo.vw_PublishedProcedureCost AS
            SELECT r.Code AS RunCode, h.Code AS HospitalCode, p.Code AS PeriodCode, pr.Code AS ProcedureCode, pr.CatalogCode, pc.*
            FROM dbo.ProcedureCost pc
            JOIN dbo.CalculationRun r ON r.Id = pc.RunId AND r.Status = 'PUBLISHED'
            JOIN dbo.Hospital h ON h.Id = r.HospitalId
            JOIN dbo.Period p ON p.Id = r.PeriodId
            JOIN dbo.[Procedure] pr ON pr.Id = pc.ProcedureId
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_PublishedProcedureCost");
        migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_PublishedCostComponent");
        migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_PublishedEncounterCost");
        migrationBuilder.Sql("ALTER TABLE dbo.ResourceRate DROP CONSTRAINT IF EXISTS CK_ResourceRate_NoOverlap");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS dbo.fn_ResourceRateOverlaps");
    }
}
