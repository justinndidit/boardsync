using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnsureUniqueSprintRanks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems");

            // Renumber every backlog by current rank order before the unique index is created,
            // so existing duplicate or fractional-decay ranks cannot violate it. This runs while
            // no unique constraint exists on (SprintId, Rank) — the one below this statement —
            // and CreateIndex validates the final state once.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT "Id",
                           (ROW_NUMBER() OVER (PARTITION BY "SprintId" ORDER BY "Rank", "Id") * 1024) AS "NewRank"
                    FROM plan."SprintWorkItems"
                )
                UPDATE plan."SprintWorkItems" sw
                SET "Rank" = ordered."NewRank"
                FROM ordered
                WHERE sw."Id" = ordered."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems",
                columns: new[] { "SprintId", "Rank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems");

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_SprintId_Rank",
                schema: "plan",
                table: "SprintWorkItems",
                columns: new[] { "SprintId", "Rank" });
        }
    }
}
