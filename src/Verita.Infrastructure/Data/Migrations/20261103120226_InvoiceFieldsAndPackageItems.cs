using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceFieldsAndPackageItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillType",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ClaimedAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CompanyShare",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CompanyTaxAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountType",
                table: "RevenueTransaction",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HisDiscountAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HisQuantity",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HisUnitPrice",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAcceptedWithDrg",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPackageInvoice",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPackageSelected",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsZeroPrice",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutlierPayment",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageProcedureId",
                table: "RevenueTransaction",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PackageSequenceNo",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PatientShare",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PatientTaxAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RejectedAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SettledAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WaivedAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaiverId",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoicePackageItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProcedureId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PackageProcedureId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PackageLineItemNo = table.Column<int>(type: "int", nullable: true),
                    PackageSequence = table.Column<int>(type: "int", nullable: true),
                    TransactionType = table.Column<int>(type: "int", nullable: false),
                    InvoiceNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LineItemNo = table.Column<int>(type: "int", nullable: false),
                    InvoiceDate = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    DiscountedCharge = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PatientShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PatientPaid = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CompanyShare = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CompanyPaid = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ChildInvoiceNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ChildInvoiceLineItemNo = table.Column<int>(type: "int", nullable: true),
                    IsCompleted = table.Column<bool>(type: "bit", nullable: true),
                    IsRefunded = table.Column<bool>(type: "bit", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoicePackageItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoicePackageItem_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InvoicePackageItem_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InvoicePackageItem_Procedure_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "Procedure",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePackageItem_EncounterId",
                table: "InvoicePackageItem",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePackageItem_HospitalId_SourceRef",
                table: "InvoicePackageItem",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePackageItem_ProcedureId",
                table: "InvoicePackageItem",
                column: "ProcedureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoicePackageItem");

            migrationBuilder.DropColumn(
                name: "BillType",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "ClaimedAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "CompanyShare",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "CompanyTaxAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "DiscountType",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "HisDiscountAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "HisQuantity",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "HisUnitPrice",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsAcceptedWithDrg",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsPackageInvoice",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsPackageSelected",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsZeroPrice",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "OutlierPayment",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "PackageProcedureId",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "PackageSequenceNo",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "PatientShare",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "PatientTaxAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "RejectedAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "SettledAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "WaivedAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "WaiverId",
                table: "RevenueTransaction");
        }
    }
}
