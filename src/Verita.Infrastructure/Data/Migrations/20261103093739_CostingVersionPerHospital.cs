using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CostingVersionPerHospital : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CostingVersion_Code",
                table: "CostingVersion");

            migrationBuilder.AddColumn<Guid>(
                name: "HospitalId",
                table: "CostingVersion",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CostingVersionHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.Sql(CostingVersionSplit.Sql);

            migrationBuilder.CreateIndex(
                name: "IX_CostingVersion_HospitalId_Code",
                table: "CostingVersion",
                columns: new[] { "HospitalId", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CostingVersion_Hospital_HospitalId",
                table: "CostingVersion",
                column: "HospitalId",
                principalTable: "Hospital",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only: the per-hospital copies are not merged back, so this fails while they exist. Restore a backup to undo the split.
            migrationBuilder.DropForeignKey(
                name: "FK_CostingVersion_Hospital_HospitalId",
                table: "CostingVersion");

            migrationBuilder.DropIndex(
                name: "IX_CostingVersion_HospitalId_Code",
                table: "CostingVersion");

            migrationBuilder.DropColumn(
                name: "HospitalId",
                table: "CostingVersion")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CostingVersionHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_CostingVersion_Code",
                table: "CostingVersion",
                column: "Code",
                unique: true);
        }
    }
}
