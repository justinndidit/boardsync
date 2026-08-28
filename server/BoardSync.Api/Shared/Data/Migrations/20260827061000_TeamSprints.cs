using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class TeamSprints : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Sprints move from projects to teams — see <c>docs/adr-001-team-sprints.md</c>.
        /// </para>
        /// <para>
        /// <b>Not a column rename.</b> EF scaffolded one, and it would have left every existing row
        /// holding a project id in a team column: structurally valid, silently wrong, and only
        /// discovered when a foreign key rejected it or a sprint appeared under a team that never
        /// ran it. The column is added, backfilled through the project's assigned team, and the old
        /// one dropped.
        /// </para>
        /// <para>
        /// Two conflicts are resolved here that the data cannot resolve afterwards, both recorded
        /// in the ADR: duplicate sprint numbers within a team, and a team ending up with more than
        /// one active sprint.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sprints_Projects_ProjectId",
                schema: "plan",
                table: "Sprints");

            migrationBuilder.DropIndex(name: "IX_Sprints_ProjectId_Status", schema: "plan", table: "Sprints");
            migrationBuilder.DropIndex(name: "IX_Sprints_ProjectId_Number", schema: "plan", table: "Sprints");
            migrationBuilder.DropIndex(name: "IX_Sprints_ProjectId", schema: "plan", table: "Sprints");

            // Nullable while it is filled in; tightened below once every row has a team.
            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                schema: "plan",
                table: "Sprints",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE plan."Sprints" AS s
                SET    "TeamId" = p."AssignedTeamId"
                FROM   org."Projects" AS p
                WHERE  p."Id" = s."ProjectId";
                """);

            // A sprint whose project has since been deleted has no team to belong to and no way to
            // acquire one. Deleted rather than left orphaned: it is unreachable either way, and a
            // null team would fail the constraint below.
            migrationBuilder.Sql("""
                DELETE FROM plan."SprintWorkItems"
                WHERE  "SprintId" IN (SELECT "Id" FROM plan."Sprints" WHERE "TeamId" IS NULL);

                DELETE FROM plan."Sprints" WHERE "TeamId" IS NULL;
                """);

            // Two projects of one team could each have an active sprint, which the new model
            // forbids. The most recently started stays active; the rest are completed rather than
            // deleted, so their history and their velocity point survive.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "TeamId"
                               ORDER BY "StartDate" DESC, "Id" DESC
                           ) AS rn
                    FROM   plan."Sprints"
                    -- Status is persisted as its name, not its ordinal.
                    WHERE  "Status" = 'Active'
                )
                UPDATE plan."Sprints" AS s
                SET    "Status" = 'Completed'
                FROM   ranked
                WHERE  ranked."Id" = s."Id" AND ranked.rn > 1;
                """);

            // Numbers were per project, so one team can now hold several Sprint 1s. Renumbered
            // chronologically, which is the order a team would recite them in.
            migrationBuilder.Sql("""
                WITH renumbered AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "TeamId"
                               ORDER BY "StartDate", "Id"
                           ) AS n
                    FROM   plan."Sprints"
                )
                UPDATE plan."Sprints" AS s
                SET    "Number" = renumbered.n
                FROM   renumbered
                WHERE  renumbered."Id" = s."Id";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "TeamId",
                schema: "plan",
                table: "Sprints",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.DropColumn(name: "ProjectId", schema: "plan", table: "Sprints");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId", schema: "plan", table: "Sprints", column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId_Status",
                schema: "plan", table: "Sprints", columns: ["TeamId", "Status"]);

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId_Number",
                schema: "plan", table: "Sprints", columns: ["TeamId", "Number"], unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Sprints_Teams_TeamId",
                schema: "plan",
                table: "Sprints",
                column: "TeamId",
                principalSchema: "org",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately not reversible.
            //
            // Going back means giving every sprint a single project, and a team sprint holding work
            // from three of them has no such answer — picking one would silently discard the rest.
            // The renumbering and the completed-sprint resolution are lossy in the same way.
            //
            // Restore from a backup taken before this ran.
            throw new NotSupportedException(
                "TeamSprints cannot be reverted: a team sprint spanning several projects has no " +
                "single project to return to. Restore from a backup taken before it was applied.");
        }
    }
}
