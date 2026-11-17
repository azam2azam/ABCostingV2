using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsumptionAndStaffTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumptionItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServiceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LinkMethod = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OtReservationId = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemKeySystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OracleItemCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConversionFactor = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    PrescribedQty = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    CostCentreCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BilledAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    HisUnitPrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumptionItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsumptionItem_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConsumptionItem_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "StaffTime",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServiceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LinkMethod = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OtReservationId = table.Column<int>(type: "int", nullable: true),
                    StaffRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StaffType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StaffId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Minutes = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Basis = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Volume = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    VolumeUom = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CostCentreCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffTime", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffTime_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StaffTime_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StaffTime_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Provider",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumptionItem_EncounterId",
                table: "ConsumptionItem",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumptionItem_HospitalId_HisEncounterNo",
                table: "ConsumptionItem",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumptionItem_HospitalId_SourceRef",
                table: "ConsumptionItem",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffTime_EncounterId",
                table: "StaffTime",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffTime_HospitalId_HisEncounterNo",
                table: "StaffTime",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffTime_HospitalId_SourceRef",
                table: "StaffTime",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffTime_ProviderId",
                table: "StaffTime",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumptionItem");

            migrationBuilder.DropTable(
                name: "StaffTime");
        }
    }
}
