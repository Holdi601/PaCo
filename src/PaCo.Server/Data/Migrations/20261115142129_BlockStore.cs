using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlockStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlockStore",
                table: "Children",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockStore",
                table: "Children");
        }
    }
}
