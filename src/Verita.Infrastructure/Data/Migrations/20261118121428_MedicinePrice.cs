using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MedicinePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MedicinePrice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MedicineCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GenericName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Strength = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PackQuantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    QuantityUnit = table.Column<int>(type: "int", nullable: true),
                    StdSellingPrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    StdPurchasePrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    Vat = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    HisStatus = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicinePrice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicinePrice_Hospital_HospitalId",
                        column: x => x.HospitalId,
                        principalTable: "Hospital",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MedicinePrice_HospitalId_MedicineCode",
                table: "MedicinePrice",
                columns: new[] { "HospitalId", "MedicineCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MedicinePrice");
        }
    }
}
