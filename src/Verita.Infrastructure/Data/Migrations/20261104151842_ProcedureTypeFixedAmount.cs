using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProcedureTypeFixedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FixedAmount",
                table: "ProcedureTypeCosting",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FixedAmount",
                table: "ProcedureTypeCosting");
        }
    }
}
