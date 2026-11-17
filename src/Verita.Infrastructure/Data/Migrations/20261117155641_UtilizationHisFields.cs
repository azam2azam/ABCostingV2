using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UtilizationHisFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClinicId",
                table: "StaffTime",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndTime",
                table: "StaffTime",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OtId",
                table: "StaffTime",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartTime",
                table: "StaffTime",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BedId",
                table: "EncounterTransfer",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Charges",
                table: "EncounterTransfer",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DoctorId",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NursingStationId",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PatientStatus",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerformingClinicId",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerformingDoctorId",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "EncounterTransfer",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomId",
                table: "EncounterTransfer",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferType",
                table: "EncounterTransfer",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BillingPrice",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HisGrossAmount",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HisSource",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HisTransType",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemCategoryId",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarkUp",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NursingStationId",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderLineItemNo",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderNo",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundableQty",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellingPrice",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockQtySnapshot",
                table: "ConsumptionItem",
                type: "decimal(19,6)",
                precision: 19,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubCategoryId",
                table: "ConsumptionItem",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClinicId",
                table: "StaffTime");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "StaffTime");

            migrationBuilder.DropColumn(
                name: "OtId",
                table: "StaffTime");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "StaffTime");

            migrationBuilder.DropColumn(
                name: "BedId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "Charges",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "DoctorId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "NursingStationId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "PatientStatus",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "PerformingClinicId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "PerformingDoctorId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "TransferType",
                table: "EncounterTransfer");

            migrationBuilder.DropColumn(
                name: "BillingPrice",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "HisGrossAmount",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "HisSource",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "HisTransType",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "ItemCategoryId",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "MarkUp",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "NursingStationId",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "OrderLineItemNo",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "OrderNo",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "RefundableQty",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "SellingPrice",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "StockQtySnapshot",
                table: "ConsumptionItem");

            migrationBuilder.DropColumn(
                name: "SubCategoryId",
                table: "ConsumptionItem");
        }
    }
}
