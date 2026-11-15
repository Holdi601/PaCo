using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class WifiDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NearbyJson",
                table: "Locations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "DiagnosticsRequestedUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FamilyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtectedText = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Diagnostics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Diagnostics_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Diagnostics_DeviceId",
                table: "Diagnostics",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Diagnostics");

            migrationBuilder.DropColumn(
                name: "NearbyJson",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "DiagnosticsRequestedUtc",
                table: "Devices");
        }
    }
}
