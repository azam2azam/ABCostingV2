using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaimsReceivedStored : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ClaimsReceivedLines",
                table: "RunPackageDoctor",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ClaimsReceived",
                table: "RunPackageBilling",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimsReceivedLines",
                table: "RunPackageDoctor");

            migrationBuilder.DropColumn(
                name: "ClaimsReceived",
                table: "RunPackageBilling");
        }
    }
}
