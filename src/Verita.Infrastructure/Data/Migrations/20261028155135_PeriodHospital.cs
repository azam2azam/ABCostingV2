using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PeriodHospital : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HospitalId",
                table: "Period",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Period_HospitalId",
                table: "Period",
                column: "HospitalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Period_Hospital_HospitalId",
                table: "Period",
                column: "HospitalId",
                principalTable: "Hospital",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Period_Hospital_HospitalId",
                table: "Period");

            migrationBuilder.DropIndex(
                name: "IX_Period_HospitalId",
                table: "Period");

            migrationBuilder.DropColumn(
                name: "HospitalId",
                table: "Period");
        }
    }
}
