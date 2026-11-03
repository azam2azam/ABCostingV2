using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HisStatusAndLineState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HisLineStatus",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPerformed",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRefunded",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReturned",
                table: "RevenueTransaction",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerformType",
                table: "RevenueTransaction",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundedAmount",
                table: "RevenueTransaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HisArrivedAt",
                table: "Encounter",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HisErStatus",
                table: "Encounter",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HisStatus",
                table: "Encounter",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HisVisitClosed",
                table: "Encounter",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HisLineStatus",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsPerformed",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsRefunded",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "IsReturned",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "PerformType",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "RefundedAmount",
                table: "RevenueTransaction");

            migrationBuilder.DropColumn(
                name: "HisArrivedAt",
                table: "Encounter");

            migrationBuilder.DropColumn(
                name: "HisErStatus",
                table: "Encounter");

            migrationBuilder.DropColumn(
                name: "HisStatus",
                table: "Encounter");

            migrationBuilder.DropColumn(
                name: "HisVisitClosed",
                table: "Encounter");
        }
    }
}
