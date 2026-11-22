using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SilentDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SilentAlertDays",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7); // existing PCs get the default too (not "off")

            migrationBuilder.AddColumn<long>(
                name: "SilentAlertSentUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UninstalledUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SilentAlertDays",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SilentAlertSentUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UninstalledUtc",
                table: "Devices");
        }
    }
}
