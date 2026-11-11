using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HospitalLocalProcedureCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Procedure_Code",
                table: "Procedure");

            migrationBuilder.DropIndex(
                name: "IX_Procedure_HospitalId",
                table: "Procedure");

            migrationBuilder.DropIndex(
                name: "IX_Package_Code",
                table: "Package");

            migrationBuilder.DropIndex(
                name: "IX_Package_HospitalId",
                table: "Package");

            // A procedure or package code is now unique within its hospital, so the hospital prefix ("SWD-AGL.01002003") goes:
            // 01002003 is 01002003 in every hospital. Codes without the prefix are left as they are.
            migrationBuilder.Sql(@"
                UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
                FROM [Procedure] p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;
                UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
                FROM Package p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;");

            migrationBuilder.CreateIndex(
                name: "IX_Procedure_HospitalId_Code",
                table: "Procedure",
                columns: new[] { "HospitalId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Package_HospitalId_Code",
                table: "Package",
                columns: new[] { "HospitalId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Procedure_HospitalId_Code",
                table: "Procedure");

            migrationBuilder.DropIndex(
                name: "IX_Package_HospitalId_Code",
                table: "Package");

            // Back to hospital-prefixed codes, unique across the group.
            migrationBuilder.Sql(@"
                UPDATE p SET p.Code = h.Code + '.' + p.Code
                FROM [Procedure] p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) <> h.Code + '.';
                UPDATE p SET p.Code = h.Code + '.' + p.Code
                FROM Package p JOIN Hospital h ON h.Id = p.HospitalId
                WHERE LEFT(p.Code, LEN(h.Code) + 1) <> h.Code + '.';");

            migrationBuilder.CreateIndex(
                name: "IX_Procedure_Code",
                table: "Procedure",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Procedure_HospitalId",
                table: "Procedure",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Package_Code",
                table: "Package",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Package_HospitalId",
                table: "Package",
                column: "HospitalId");
        }
    }
}
