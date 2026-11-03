using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefundTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefundTransaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PatientMrn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProcedureId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RefundNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RefundType = table.Column<int>(type: "int", nullable: false),
                    RefundDate = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    OriginalTransactionType = table.Column<int>(type: "int", nullable: false),
                    OriginalInvoiceNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OriginalInvoiceDate = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvoiceLineNo = table.Column<int>(type: "int", nullable: false),
                    InvoiceCancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RefundAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PatientShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CompanyShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PatientPaid = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    HeaderRefundAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    HeaderAmountPaid = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "int", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReasonId = table.Column<int>(type: "int", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefundTransaction_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RefundTransaction_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RefundTransaction_Payer_PayerId",
                        column: x => x.PayerId,
                        principalTable: "Payer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RefundTransaction_Procedure_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "Procedure",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundTransaction_EncounterId",
                table: "RefundTransaction",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundTransaction_HospitalId_SourceRef",
                table: "RefundTransaction",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundTransaction_PayerId",
                table: "RefundTransaction",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundTransaction_ProcedureId",
                table: "RefundTransaction",
                column: "ProcedureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundTransaction");
        }
    }
}
