using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssistantRoutedPhrases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantRoutedPhrase",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Intent = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hits = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRoutedPhrase", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRoutedPhrase_TextKey",
                table: "AssistantRoutedPhrase",
                column: "TextKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantRoutedPhrase");
        }
    }
}
