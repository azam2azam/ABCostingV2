using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportFile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsHisPack = table.Column<bool>(type: "bit", nullable: false),
                    HisSetupId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HisProjectId = table.Column<int>(type: "int", nullable: true),
                    Pack = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FactsFrom = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FactsTo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Hospital = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastDryRun = table.Column<bool>(type: "bit", nullable: false),
                    TablesTotal = table.Column<int>(type: "int", nullable: false),
                    TablesDone = table.Column<int>(type: "int", nullable: false),
                    CurrentTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RunBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowsCreated = table.Column<int>(type: "int", nullable: false),
                    RowsUpdated = table.Column<int>(type: "int", nullable: false),
                    RowsUnchanged = table.Column<int>(type: "int", nullable: false),
                    RowsRejected = table.Column<int>(type: "int", nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportFile", x => x.Id);
                    table.CheckConstraint("CK_ImportFile_ContextJson_Json", "[ContextJson] IS NULL OR ISJSON([ContextJson]) = 1");
                    table.CheckConstraint("CK_ImportFile_DetailJson_Json", "[DetailJson] IS NULL OR ISJSON([DetailJson]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "ImportFileContent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportFileContent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportFileContent_ImportFile_Id",
                        column: x => x.Id,
                        principalTable: "ImportFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportFile_UploadedAt",
                table: "ImportFile",
                column: "UploadedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportFileContent");

            migrationBuilder.DropTable(
                name: "ImportFile");
        }
    }
}
