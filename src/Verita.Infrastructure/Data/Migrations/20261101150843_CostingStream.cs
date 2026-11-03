using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CostingStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureMargin_ProcedureId",
                table: "ProcedureMargin");

            migrationBuilder.AddColumn<string>(
                name: "Stream",
                table: "ProcedureTypeCosting",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "DIRECT");

            migrationBuilder.AddColumn<string>(
                name: "Stream",
                table: "ProcedureMargin",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "DIRECT");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType_Stream",
                table: "ProcedureTypeCosting",
                columns: new[] { "HospitalId", "ProcType", "Stream" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT')");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureMargin_ProcedureId_Stream",
                table: "ProcedureMargin",
                columns: new[] { "ProcedureId", "Stream" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropIndex(
                name: "IX_ProcedureMargin_ProcedureId_Stream",
                table: "ProcedureMargin");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin");

            migrationBuilder.DropColumn(
                name: "Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropColumn(
                name: "Stream",
                table: "ProcedureMargin");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureTypeCosting_HospitalId_ProcType",
                table: "ProcedureTypeCosting",
                columns: new[] { "HospitalId", "ProcType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureMargin_ProcedureId",
                table: "ProcedureMargin",
                column: "ProcedureId",
                unique: true);
        }
    }
}
