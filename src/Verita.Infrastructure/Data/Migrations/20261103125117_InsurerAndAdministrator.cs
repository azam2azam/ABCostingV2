using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InsurerAndAdministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdministratorId",
                table: "Payer",
                type: "uniqueidentifier",
                nullable: true)
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PayerHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.AddColumn<Guid>(
                name: "InsurerId",
                table: "Payer",
                type: "uniqueidentifier",
                nullable: true)
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PayerHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateTable(
                name: "Insurer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Insurer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerAdministrator",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayerAdministrator", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payer_AdministratorId",
                table: "Payer",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Payer_InsurerId",
                table: "Payer",
                column: "InsurerId");

            migrationBuilder.CreateIndex(
                name: "IX_Insurer_Code",
                table: "Insurer",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayerAdministrator_Code",
                table: "PayerAdministrator",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Payer_Insurer_InsurerId",
                table: "Payer",
                column: "InsurerId",
                principalTable: "Insurer",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Payer_PayerAdministrator_AdministratorId",
                table: "Payer",
                column: "AdministratorId",
                principalTable: "PayerAdministrator",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payer_Insurer_InsurerId",
                table: "Payer");

            migrationBuilder.DropForeignKey(
                name: "FK_Payer_PayerAdministrator_AdministratorId",
                table: "Payer");

            migrationBuilder.DropTable(
                name: "Insurer");

            migrationBuilder.DropTable(
                name: "PayerAdministrator");

            migrationBuilder.DropIndex(
                name: "IX_Payer_AdministratorId",
                table: "Payer");

            migrationBuilder.DropIndex(
                name: "IX_Payer_InsurerId",
                table: "Payer");

            migrationBuilder.DropColumn(
                name: "AdministratorId",
                table: "Payer")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PayerHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.DropColumn(
                name: "InsurerId",
                table: "Payer")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PayerHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");
        }
    }
}
