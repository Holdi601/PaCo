using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertsScreenshotsNtfy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NtfyTopic",
                table: "Families",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlertsJson",
                table: "Children",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "NotifyChildOnScreenshot",
                table: "Children",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FamilyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Keyword = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    App = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    WindowTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Snippet = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReadUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Children_ChildId",
                        column: x => x.ChildId,
                        principalTable: "Children",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Screenshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FamilyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Size = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Screenshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Screenshots_Children_ChildId",
                        column: x => x.ChildId,
                        principalTable: "Children",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ChildId",
                table: "Alerts",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_FamilyId_TimeUtc",
                table: "Alerts",
                columns: new[] { "FamilyId", "TimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_ChildId_RequestedUtc",
                table: "Screenshots",
                columns: new[] { "ChildId", "RequestedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "Screenshots");

            migrationBuilder.DropColumn(
                name: "NtfyTopic",
                table: "Families");

            migrationBuilder.DropColumn(
                name: "AlertsJson",
                table: "Children");

            migrationBuilder.DropColumn(
                name: "NotifyChildOnScreenshot",
                table: "Children");
        }
    }
}
