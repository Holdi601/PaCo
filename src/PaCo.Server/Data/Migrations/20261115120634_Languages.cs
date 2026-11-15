using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Languages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArgsJson",
                table: "DeviceEvents",
                type: "TEXT",
                maxLength: 1500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Template",
                table: "DeviceEvents",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Children",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "de");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArgsJson",
                table: "DeviceEvents");

            migrationBuilder.DropColumn(
                name: "Template",
                table: "DeviceEvents");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Children");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "AspNetUsers");
        }
    }
}
