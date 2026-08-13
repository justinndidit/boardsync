using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_FractionalRanks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Rank",
                schema: "plan",
                table: "SprintWorkItems",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            // Existing rows all default to rank 0, which would leave every backlog in arbitrary
            // order. Seed ranks from the Position they already have so current ordering survives,
            // spaced by the same step new items use so there is room to insert between them.
            migrationBuilder.Sql("""
                UPDATE plan."SprintWorkItems"
                SET "Rank" = ("Position" + 1) * 1024
                WHERE "Rank" = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems",
                columns: new[] { "SprintId", "Rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems");

            migrationBuilder.DropColumn(
                name: "Rank",
                schema: "plan",
                table: "SprintWorkItems");
        }
    }
}
