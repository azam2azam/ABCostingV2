using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZeroPriceFixedCost : Migration
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

            migrationBuilder.AddColumn<decimal>(
                name: "FixedAmount",
                table: "ProcedureMargin",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureTypeCosting_Stream",
                table: "ProcedureTypeCosting",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT', N'ZERO_FIXED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureMargin_Stream",
                table: "ProcedureMargin",
                sql: "[Stream] IN (N'DIRECT', N'INDIRECT', N'PACKAGE_DIRECT', N'ZERO_FIXED')");
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

            migrationBuilder.DropColumn(
                name: "FixedAmount",
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
    }
}
