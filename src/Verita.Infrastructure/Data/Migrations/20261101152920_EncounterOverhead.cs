using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncounterOverhead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncounterOverhead",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: false),
                    OverheadPct = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    OverheadSar = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncounterOverhead", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EncounterOverhead_CostCenter_CostCenterId",
                        column: x => x.CostCenterId,
                        principalTable: "CostCenter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EncounterOverhead_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncounterOverhead_CostCenterId",
                table: "EncounterOverhead",
                column: "CostCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterOverhead_HospitalId_CostCenterId",
                table: "EncounterOverhead",
                columns: new[] { "HospitalId", "CostCenterId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncounterOverhead");
        }
    }
}
