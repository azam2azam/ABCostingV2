using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verita.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssistantThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantThread",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantThread", x => x.Id);
                    table.CheckConstraint("CK_AssistantThread_ContextJson_Json", "[ContextJson] IS NULL OR ISJSON([ContextJson]) = 1");
                    table.ForeignKey(
                        name: "FK_AssistantThread_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AssistantMessage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: false),
                    Intent = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TurnType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ParamsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200, nullable: true),
                    Permission = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RunCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Matched = table.Column<bool>(type: "bit", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Feedback = table.Column<int>(type: "int", nullable: true),
                    FeedbackReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMessage", x => x.Id);
                    table.CheckConstraint("CK_AssistantMessage_DetailJson_Json", "[DetailJson] IS NULL OR ISJSON([DetailJson]) = 1");
                    table.CheckConstraint("CK_AssistantMessage_ParamsJson_Json", "[ParamsJson] IS NULL OR ISJSON([ParamsJson]) = 1");
                    table.CheckConstraint("CK_AssistantMessage_Role", "[Role] IN (N'user', N'assistant')");
                    table.ForeignKey(
                        name: "FK_AssistantMessage_AssistantThread_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "AssistantThread",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMessage_Matched_CreatedAt",
                table: "AssistantMessage",
                columns: new[] { "Matched", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMessage_ThreadId_CreatedAt",
                table: "AssistantMessage",
                columns: new[] { "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_UserId_IsDeleted_UpdatedAt",
                table: "AssistantThread",
                columns: new[] { "UserId", "IsDeleted", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMessage");

            migrationBuilder.DropTable(
                name: "AssistantThread");
        }
    }
}
