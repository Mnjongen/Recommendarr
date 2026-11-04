using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoverAndReachability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ListedAt",
                table: "Items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ReachableShare",
                table: "EvaluationRuns",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListedAt",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ReachableShare",
                table: "EvaluationRuns");
        }
    }
}
