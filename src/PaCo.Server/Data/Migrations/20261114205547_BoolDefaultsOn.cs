using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaCo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BoolDefaultsOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time: new safety switches default to ON for children created before they existed.
            migrationBuilder.Sql("UPDATE Children SET NotifyChildOnScreenshot = 1, BlockSystemTools = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
