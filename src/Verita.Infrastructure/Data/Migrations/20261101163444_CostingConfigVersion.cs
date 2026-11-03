using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CostingConfigVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureMargin_ProcedureId_Stream",
                table: "ProcedureMargin");

            migrationBuilder.DropIndex(
                name: "IX_EncounterOverhead_HospitalId_CostCenterId",
                table: "EncounterOverhead");

            migrationBuilder.AddColumn<Guid>(
                name: "CostingVersionId",
                table: "ProcedureTypeCosting",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CostingVersionId",
                table: "ProcedureMargin",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CostingVersionId",
                table: "EncounterOverhead",
                type: "uniqueidentifier",
                nullable: true);

            const string hospitalVersion = """
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
                )
                """;
            migrationBuilder.Sql(hospitalVersion + """
                UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
                FROM ProcedureTypeCosting p
                CROSS JOIN Fallback f
                LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
                WHERE p.CostingVersionId IS NULL
                """);
            migrationBuilder.Sql(hospitalVersion + """
                UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
                FROM ProcedureMargin p
                CROSS JOIN Fallback f
                LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
                WHERE p.CostingVersionId IS NULL
                """);
            migrationBuilder.Sql(hospitalVersion + """
                UPDATE p SET p.CostingVersionId = COALESCE(hv.CostingVersionId, f.CostingVersionId)
                FROM EncounterOverhead p
                CROSS JOIN Fallback f
                LEFT JOIN HospitalVersion hv ON hv.HospitalId = p.HospitalId
                WHERE p.CostingVersionId IS NULL
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "CostingVersionId",
                table: "ProcedureTypeCosting",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CostingVersionId",
                table: "ProcedureMargin",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CostingVersionId",
                table: "EncounterOverhead",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureTypeCosting_CostingVersionId",
                table: "ProcedureTypeCosting",
                column: "CostingVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_CostingVersionId_ProcType_Stream",
                table: "ProcedureTypeCosting",
                columns: new[] { "HospitalId", "CostingVersionId", "ProcType", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureMargin_CostingVersionId",
                table: "ProcedureMargin",
                column: "CostingVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureMargin_ProcedureId_CostingVersionId_Stream",
                table: "ProcedureMargin",
                columns: new[] { "ProcedureId", "CostingVersionId", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EncounterOverhead_CostingVersionId",
                table: "EncounterOverhead",
                column: "CostingVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterOverhead_HospitalId_CostingVersionId_CostCenterId",
                table: "EncounterOverhead",
                columns: new[] { "HospitalId", "CostingVersionId", "CostCenterId" });

            migrationBuilder.AddForeignKey(
                name: "FK_EncounterOverhead_CostingVersion_CostingVersionId",
                table: "EncounterOverhead",
                column: "CostingVersionId",
                principalTable: "CostingVersion",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcedureMargin_CostingVersion_CostingVersionId",
                table: "ProcedureMargin",
                column: "CostingVersionId",
                principalTable: "CostingVersion",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcedureTypeCosting_CostingVersion_CostingVersionId",
                table: "ProcedureTypeCosting",
                column: "CostingVersionId",
                principalTable: "CostingVersion",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EncounterOverhead_CostingVersion_CostingVersionId",
                table: "EncounterOverhead");

            migrationBuilder.DropForeignKey(
                name: "FK_ProcedureMargin_CostingVersion_CostingVersionId",
                table: "ProcedureMargin");

            migrationBuilder.DropForeignKey(
                name: "FK_ProcedureTypeCosting_CostingVersion_CostingVersionId",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureTypeCosting_CostingVersionId",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_CostingVersionId_ProcType_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureMargin_CostingVersionId",
                table: "ProcedureMargin");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureMargin_ProcedureId_CostingVersionId_Stream",
                table: "ProcedureMargin");

            migrationBuilder.DropIndex(
                name: "IX_EncounterOverhead_CostingVersionId",
                table: "EncounterOverhead");

            migrationBuilder.DropIndex(
                name: "IX_EncounterOverhead_HospitalId_CostingVersionId_CostCenterId",
                table: "EncounterOverhead");

            migrationBuilder.DropColumn(
                name: "CostingVersionId",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropColumn(
                name: "CostingVersionId",
                table: "ProcedureMargin");

            migrationBuilder.DropColumn(
                name: "CostingVersionId",
                table: "EncounterOverhead");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType_Stream",
                table: "ProcedureTypeCosting",
                columns: new[] { "HospitalId", "ProcType", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureMargin_ProcedureId_Stream",
                table: "ProcedureMargin",
                columns: new[] { "ProcedureId", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EncounterOverhead_HospitalId_CostCenterId",
                table: "EncounterOverhead",
                columns: new[] { "HospitalId", "CostCenterId" });
        }
    }
}
