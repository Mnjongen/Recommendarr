using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TelegramLinkCodeExpiresAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramLinkCodeHash",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TelegramChatId",
                table: "Users",
                column: "TelegramChatId",
                unique: true,
                filter: "\"TelegramChatId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_TelegramChatId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramLinkCodeExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TelegramLinkCodeHash",
                table: "Users");
        }
    }
}
