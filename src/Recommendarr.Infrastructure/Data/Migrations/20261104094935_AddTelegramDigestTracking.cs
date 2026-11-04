using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramDigestTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TelegramDigestSentAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramDigestSentAt",
                table: "Users");
        }
    }
}
