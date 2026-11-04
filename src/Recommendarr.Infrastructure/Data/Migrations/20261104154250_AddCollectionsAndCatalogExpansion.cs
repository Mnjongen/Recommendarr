using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsAndCatalogExpansion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CollectionId",
                table: "Items",
                type: "INTEGER",
                nullable: true);

            // Re-expand every watched title on the next sync, so it picks up its collection id,
            // franchise links and the extra recommendation pages.
            migrationBuilder.Sql("UPDATE Items SET MetadataFetchedAt = NULL WHERE Id IN (SELECT ItemId FROM WatchEvents WHERE ItemId IS NOT NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectionId",
                table: "Items");
        }
    }
}
