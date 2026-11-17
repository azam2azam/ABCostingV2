using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncounterTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncounterTransfer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncounterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HisEncounterNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransferNo = table.Column<int>(type: "int", nullable: false),
                    TransferDate = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StartTime = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    EndTime = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Duration = table.Column<int>(type: "int", nullable: true),
                    HisClinicId = table.Column<int>(type: "int", nullable: true),
                    CostCentreCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncounterTransfer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EncounterTransfer_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EncounterTransfer_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncounterTransfer_EncounterId",
                table: "EncounterTransfer",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterTransfer_HospitalId_HisEncounterNo",
                table: "EncounterTransfer",
                columns: new[] { "HospitalId", "HisEncounterNo" });

            migrationBuilder.CreateIndex(
                name: "IX_EncounterTransfer_HospitalId_SourceRef",
                table: "EncounterTransfer",
                columns: new[] { "HospitalId", "SourceRef" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncounterTransfer");
        }
    }
}
