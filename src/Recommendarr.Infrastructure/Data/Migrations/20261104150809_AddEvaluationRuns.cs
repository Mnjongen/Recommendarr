using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendarr.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvaluationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Experiment = table.Column<string>(type: "TEXT", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogTitles = table.Column<int>(type: "INTEGER", nullable: false),
                    Users = table.Column<int>(type: "INTEGER", nullable: false),
                    Cases = table.Column<int>(type: "INTEGER", nullable: false),
                    Hits = table.Column<int>(type: "INTEGER", nullable: false),
                    K = table.Column<int>(type: "INTEGER", nullable: false),
                    HitRate = table.Column<double>(type: "REAL", nullable: false),
                    Mrr = table.Column<double>(type: "REAL", nullable: false),
                    MeanPercentile = table.Column<double>(type: "REAL", nullable: false),
                    EligibleShare = table.Column<double>(type: "REAL", nullable: false),
                    LinkedShare = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRuns_Experiment_StartedAt",
                table: "EvaluationRuns",
                columns: new[] { "Experiment", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationRuns");
        }
    }
}
