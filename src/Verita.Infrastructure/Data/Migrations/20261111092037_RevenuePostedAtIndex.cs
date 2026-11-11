using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RevenuePostedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RevenueTransaction_PostedAt",
                table: "RevenueTransaction",
                column: "PostedAt")
                .Annotation("SqlServer:Include", new[] { "EncounterId", "ProcedureId", "SourceRef", "HisLineStatus", "HisInvoiceType", "NetAmount", "RefundedAmount", "HisUnitPrice", "IsChildInvoice" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RevenueTransaction_PostedAt",
                table: "RevenueTransaction");
        }
    }
}
