using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageCostingStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT')");
        }
    }
}
