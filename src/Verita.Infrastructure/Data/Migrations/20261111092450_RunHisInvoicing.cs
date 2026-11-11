using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunHisInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunHisInvoicing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceType = table.Column<int>(type: "int", nullable: false),
                    Invoices = table.Column<int>(type: "int", nullable: false),
                    Encounters = table.Column<int>(type: "int", nullable: false),
                    Patients = table.Column<int>(type: "int", nullable: false),
                    Refunded = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Revenue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    MinInvoiceNo = table.Column<long>(type: "bigint", nullable: true),
                    MaxInvoiceNo = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunHisInvoicing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunHisInvoicing_CalculationRun_RunId",
                        column: x => x.RunId,
                        principalTable: "CalculationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunHisInvoicing_RunId_InvoiceType",
                table: "RunHisInvoicing",
                columns: new[] { "RunId", "InvoiceType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunHisInvoicing");
        }
    }
}
