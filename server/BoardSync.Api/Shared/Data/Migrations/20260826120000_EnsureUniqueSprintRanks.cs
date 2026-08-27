using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations;

public partial class EnsureUniqueSprintRanks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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

        migrationBuilder.DropIndex(
            name: "IX_SprintWorkItems_SprintId_Rank",
            schema: "plan",
            table: "SprintWorkItems");

        migrationBuilder.CreateIndex(
            name: "IX_SprintWorkItems_SprintId_Rank",
            schema: "plan",
            table: "SprintWorkItems",
            columns: new[] { "SprintId", "Rank" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SprintWorkItems_SprintId_Rank",
            schema: "plan",
            table: "SprintWorkItems");
    }
}