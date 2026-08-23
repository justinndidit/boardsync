using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// The QA gate: a Tester role, a self-certification setting, and a board lane for
    /// <c>InReview</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The new <c>InReview</c> work item state needs no schema change — states are stored by name —
    /// but existing boards have no column mapped to it, and a card in a state no column claims simply
    /// does not render. So this inserts the lane rather than leaving people to discover that work
    /// vanished.
    /// </para>
    /// <para>
    /// See build_context.md §4.
    /// </para>
    /// </remarks>
    public partial class PhaseB_QaGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowSelfCertification",
                schema: "org",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // ── Tester becomes assignable ────────────────────────────────────────────
            // Mirrors RolePermissions.IsValidAt, which is what the endpoints validate against. The
            // two must agree: the constraint is what makes an ill-scoped grant unrepresentable rather
            // than merely discouraged, and a request that passed the endpoint only to violate this
            // would surface as a 500.
            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                 DROP CONSTRAINT IF EXISTS "CK_RoleAssignment_RoleMatchesScope";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                  ADD CONSTRAINT "CK_RoleAssignment_RoleMatchesScope" CHECK (
                       ("OrganizationId" IS NOT NULL AND "Role" IN ('OrgAdmin', 'Member'))
                    OR ("TeamId" IS NOT NULL AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner',
                                                            'TeamMember', 'Tester', 'Viewer'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'Contributor',
                                                               'Tester', 'Viewer'))
                  );
                """);

            // ── Give existing boards a lane for InReview ─────────────────────────────
            //
            // Inserted immediately before whichever column shows Resolved, so the board still reads
            // left to right in workflow order. Guarded on the board not already having an InReview
            // column, so re-running changes nothing.
            migrationBuilder.Sql("""
                WITH target AS (
                    SELECT c."BoardId", MIN(c."Position") AS resolved_position
                      FROM plan."BoardColumns" c
                     WHERE c."MappedState" = 'Resolved'
                       AND NOT EXISTS (
                           SELECT 1 FROM plan."BoardColumns" x
                            WHERE x."BoardId" = c."BoardId" AND x."MappedState" = 'InReview')
                     GROUP BY c."BoardId"
                )
                UPDATE plan."BoardColumns" c
                   SET "Position" = c."Position" + 1
                  FROM target t
                 WHERE c."BoardId" = t."BoardId"
                   AND c."Position" >= t.resolved_position;
                """);

            migrationBuilder.Sql("""
                INSERT INTO plan."BoardColumns"
                    ("Id", "BoardId", "Name", "MappedState", "Position", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), t."BoardId", 'In Review', 'InReview',
                       t.insert_position, NOW(), NOW()
                  FROM (
                    SELECT c."BoardId", MIN(c."Position") - 1 AS insert_position
                      FROM plan."BoardColumns" c
                     WHERE c."MappedState" = 'Resolved'
                       AND NOT EXISTS (
                           SELECT 1 FROM plan."BoardColumns" x
                            WHERE x."BoardId" = c."BoardId" AND x."MappedState" = 'InReview')
                     GROUP BY c."BoardId"
                  ) t;
                """);

            // Only the seeded name is rewritten. A column somebody renamed themselves is theirs, and
            // "In Review" now belongs to the lane inserted above it.
            migrationBuilder.Sql("""
                UPDATE plan."BoardColumns"
                   SET "Name" = 'Awaiting QA', "UpdatedAt" = NOW()
                 WHERE "MappedState" = 'Resolved' AND "Name" = 'In Review';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Any work item left in InReview would be in a state no column shows, so it goes back to
            // Active — where it came from — before the lane is removed.
            migrationBuilder.Sql("""
                UPDATE work."WorkItems" SET "State" = 'Active' WHERE "State" = 'InReview';
                """);

            migrationBuilder.Sql("""
                DELETE FROM plan."BoardColumns" WHERE "MappedState" = 'InReview';
                """);

            migrationBuilder.Sql("""
                UPDATE plan."BoardColumns"
                   SET "Name" = 'In Review'
                 WHERE "MappedState" = 'Resolved' AND "Name" = 'Awaiting QA';
                """);

            migrationBuilder.Sql("""
                DELETE FROM iam."RoleAssignments" WHERE "Role" = 'Tester';
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                 DROP CONSTRAINT IF EXISTS "CK_RoleAssignment_RoleMatchesScope";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                  ADD CONSTRAINT "CK_RoleAssignment_RoleMatchesScope" CHECK (
                       ("OrganizationId" IS NOT NULL AND "Role" IN ('OrgAdmin', 'Member'))
                    OR ("TeamId" IS NOT NULL AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner',
                                                            'TeamMember', 'Viewer'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'Contributor', 'Viewer'))
                  );
                """);

            migrationBuilder.DropColumn(
                name: "AllowSelfCertification",
                schema: "org",
                table: "Projects");
        }
    }
}
