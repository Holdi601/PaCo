using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceUpdateRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UpdateRequested",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateRequested",
                table: "Devices");
        }
    }
}
