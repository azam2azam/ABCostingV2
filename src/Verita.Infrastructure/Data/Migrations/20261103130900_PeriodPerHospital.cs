using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PeriodPerHospital : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Period_Code",
                table: "Period");

            migrationBuilder.DropIndex(
                name: "IX_Period_HospitalId",
                table: "Period");

            migrationBuilder.CreateIndex(
                name: "IX_Period_HospitalId_Code",
                table: "Period",
                columns: new[] { "HospitalId", "Code" },
                unique: true,
                filter: "[HospitalId] IS NOT NULL");

            migrationBuilder.Sql(Verita.Infrastructure.Data.OpenPeriods.AssignSharedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Period_HospitalId_Code",
                table: "Period");

            migrationBuilder.CreateIndex(
                name: "IX_Period_Code",
                table: "Period",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Period_HospitalId",
                table: "Period",
                column: "HospitalId");
        }
    }
}
