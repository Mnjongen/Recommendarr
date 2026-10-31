using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeerrUserSetManually : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeerrUserSetManually",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeerrUserSetManually",
                table: "Users");
        }
    }
}
