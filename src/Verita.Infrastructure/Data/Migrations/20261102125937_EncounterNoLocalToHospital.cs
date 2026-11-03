using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncounterNoLocalToHospital : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Encounter_EncounterNo",
                table: "Encounter");

            // The number alone: {hospital}-{OP|IP|ER}-N and {hospital}-N both become N.
            migrationBuilder.Sql("""
                UPDATE e
                SET EncounterNo = CASE
                    WHEN LEFT(x.Local, 3) IN ('OP-', 'IP-', 'ER-') THEN SUBSTRING(x.Local, 4, LEN(x.Local))
                    ELSE x.Local END
                FROM Encounter e
                JOIN Hospital h ON h.Id = e.HospitalId
                CROSS APPLY (SELECT SUBSTRING(e.EncounterNo, LEN(h.Code) + 2, LEN(e.EncounterNo)) AS Local) x
                WHERE LEFT(e.EncounterNo, LEN(h.Code) + 1) = h.Code + '-';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_HospitalId_EncounterNo",
                table: "Encounter",
                columns: new[] { "HospitalId", "EncounterNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Encounter_HospitalId_EncounterNo",
                table: "Encounter");

            // {hospital}-N keeps numbers unique across hospitals; the old OP/IP/ER part is not restored.
            migrationBuilder.Sql("""
                UPDATE e SET EncounterNo = h.Code + '-' + e.EncounterNo
                FROM Encounter e
                JOIN Hospital h ON h.Id = e.HospitalId;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_EncounterNo",
                table: "Encounter",
                column: "EncounterNo",
                unique: true);
        }
    }
}
