using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChildInvoiceLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HisInvoiceType",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HisMainInvoiceNo",
                table: "RevenueTransaction",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsChildInvoice",
                table: "RevenueTransaction",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsChildInvoice",
                table: "EncounterService",
                type: "bit",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql(Verita.Infrastructure.Data.OpenPeriods.MarkChildInvoiceLinesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HisInvoiceType",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "HisMainInvoiceNo",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsChildInvoice",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsChildInvoice",
                table: "EncounterService");
        }
    }
}
