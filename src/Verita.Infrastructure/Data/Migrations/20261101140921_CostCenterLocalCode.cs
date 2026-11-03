using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CostCenterLocalCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CostCenter_Code",
                table: "CostCenter");

            migrationBuilder.DropIndex(
                name: "IX_CostCenter_HospitalId",
                table: "CostCenter");

            migrationBuilder.Sql("""
                UPDATE cc
                SET Code = SUBSTRING(cc.Code, LEN(h.Code) + 2, 200)
                FROM CostCenter cc
                INNER JOIN Hospital h ON h.Id = cc.HospitalId
                WHERE cc.Code LIKE h.Code + '.%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CostCenter_HospitalId_Code",
                table: "CostCenter",
                columns: new[] { "HospitalId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CostCenter_HospitalId_Code",
                table: "CostCenter");

            migrationBuilder.Sql("""
                UPDATE cc
                SET Code = h.Code + '.' + cc.Code
                FROM CostCenter cc
                INNER JOIN Hospital h ON h.Id = cc.HospitalId
                WHERE cc.Code NOT LIKE h.Code + '.%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CostCenter_Code",
                table: "CostCenter",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostCenter_HospitalId",
                table: "CostCenter",
                column: "HospitalId");
        }
    }
}
