using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZeroPriceProcedureCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureCost_CostType",
                table: "ProcedureCost");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureCost_CostType",
                table: "ProcedureCost",
                sql: "[CostType] IN (N'STANDARD', N'ACTUAL', N'BUDGET', N'FORECAST', N'TARGET', N'ZERO_PRICE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcedureCost_CostType",
                table: "ProcedureCost");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcedureCost_CostType",
                table: "ProcedureCost",
                sql: "[CostType] IN (N'STANDARD', N'ACTUAL', N'BUDGET', N'FORECAST', N'TARGET')");
        }
    }
}
