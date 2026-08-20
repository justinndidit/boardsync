using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Makes "a sprint belongs to a project" true of the data, then of the schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AddSprintProjectId</c> renamed <c>Sprints.TeamId</c> to <c>ProjectId</c> without
    /// translating the values, so in any environment that already held sprints the column now
    /// contains team ids under a project's name. That is not a cosmetic problem: authorization
    /// resolves a sprint through <c>ProjectId</c>, and a team id there resolves to a project nobody
    /// holds a grant on, denying every caller including an organization administrator.
    /// </para>
    /// <para>
    /// The remap runs first and the foreign key second, so the key is what proves the remap was
    /// complete — a row still holding a team id cannot satisfy it, and the migration fails rather
    /// than leaving the sprint module quietly inaccessible.
    /// </para>
    /// <para>
    /// A team serving several projects has no single right answer, and neither does a team serving
    /// none. Both raise instead of guessing: picking one would silently move a sprint's work under a
    /// project nobody chose, which is worse than a failed deploy that says exactly what it found.
    /// </para>
    /// </remarks>
    public partial class Stage4_SprintBelongsToProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    stranded text;
                BEGIN
                    -- Nothing to do where the column already holds project ids: either the rename
                    -- predates any sprint, or this has run before.
                    IF NOT EXISTS (
                        SELECT 1 FROM plan."Sprints" s
                         WHERE EXISTS (SELECT 1 FROM org."Teams" t WHERE t."Id" = s."ProjectId")
                    ) THEN
                        RETURN;
                    END IF;

                    -- Refuse to guess. A team with anything other than exactly one active project
                    -- cannot be resolved to "the" project its sprints belong to.
                    SELECT string_agg(DISTINCT s."ProjectId"::text, ', ')
                      INTO stranded
                      FROM plan."Sprints" s
                      JOIN org."Teams" t ON t."Id" = s."ProjectId"
                     WHERE (SELECT count(*) FROM org."Projects" p
                             WHERE p."AssignedTeamId" = t."Id" AND p."IsActive") <> 1;

                    IF stranded IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Cannot map sprints to a project for team(s) %: each team must have exactly one active project. Reassign or archive projects, or move these sprints by hand, then re-run.', stranded;
                    END IF;

                    UPDATE plan."Sprints" s
                       SET "ProjectId" = p."Id"
                      FROM org."Projects" p
                     WHERE p."AssignedTeamId" = s."ProjectId"
                       AND p."IsActive"
                       AND EXISTS (SELECT 1 FROM org."Teams" t WHERE t."Id" = s."ProjectId");
                END $$;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Sprints_Projects_ProjectId",
                schema: "plan",
                table: "Sprints",
                column: "ProjectId",
                principalSchema: "org",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sprints_Projects_ProjectId",
                schema: "plan",
                table: "Sprints");
        }
    }
}
