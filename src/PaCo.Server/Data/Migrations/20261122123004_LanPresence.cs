using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LanPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LanAlert",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // on by default, like for new PCs

            migrationBuilder.AddColumn<long>(
                name: "LanAlertSentUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanMacsJson",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<long>(
                name: "LanSeenUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LanSinceUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SuspendedUtc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LanAlert",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LanAlertSentUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LanMacsJson",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LanSeenUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LanSinceUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SuspendedUtc",
                table: "Devices");
        }
    }
}
