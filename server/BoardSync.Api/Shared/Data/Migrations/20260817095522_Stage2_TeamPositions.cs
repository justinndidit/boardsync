using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Makes the role/scope pairing and the single-holder rule for team positions structural.
    /// </summary>
    /// <remarks>
    /// Written by hand. The scaffolder also wanted to add an <c>xmin</c> column to
    /// <c>work.WorkItems</c>; that is Postgres' own system column, mapped as a concurrency token in
    /// <c>BoardSyncDbContext</c> and deliberately backed by no column of ours, so creating it would
    /// fail. It is left out.
    /// </remarks>
    public partial class Stage2_TeamPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Normalise organization-scope roles that never meant anything ──────────
            // 'ProjectAdmin' and 'TeamMember' at organization scope were assignable but inert: the
            // old ladder let them satisfy an organization read check and nothing else, because only
            // OrgAdmin ever inherited downwards. 'Reader' is exactly that power stated plainly, so
            // this preserves what those rows actually granted rather than changing it.
            //
            // Deleted first where the user already holds Reader in the same organization, or the
            // update would collide with the partial unique index on (UserId, Role, OrganizationId).
            migrationBuilder.Sql("""
                DELETE FROM iam."RoleAssignments" a
                 WHERE a."OrganizationId" IS NOT NULL
                   AND a."Role" IN ('ProjectAdmin', 'TeamMember')
                   AND EXISTS (
                       SELECT 1 FROM iam."RoleAssignments" b
                        WHERE b."OrganizationId" = a."OrganizationId"
                          AND b."UserId" = a."UserId"
                          AND b."Role" = 'Reader');

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Reader'
                 WHERE "OrganizationId" IS NOT NULL
                   AND "Role" IN ('ProjectAdmin', 'TeamMember');
                """);

            // ── A role must make sense at the scope it is held ────────────────────────
            // Previously nothing stopped OrgAdmin being assigned at team scope: the row satisfied
            // every team check by rank while meaning nothing. With roles now resolved through a
            // permission table rather than a ladder, an unmapped pairing grants nothing silently —
            // so it is rejected outright instead.
            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                  ADD CONSTRAINT "CK_RoleAssignment_RoleMatchesScope" CHECK (
                       ("OrganizationId" IS NOT NULL AND "Role" IN ('OrgAdmin', 'Reader'))
                    OR ("TeamId" IS NOT NULL AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner', 'TeamMember', 'Reader'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'TeamMember', 'Reader'))
                  );
                """);

            // ── One holder per position per team ─────────────────────────────────────
            // Partial, like the existing uniqueness indexes and for the same reason: Postgres treats
            // NULLs as distinct, so an unfiltered index over a nullable TeamId would constrain
            // nothing. This is what makes "who is the Scrum Master" a question with one answer, and
            // what makes a transfer a replacement rather than an accumulation.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_RoleAssignments_OneHolderPerTeamPosition"
                    ON iam."RoleAssignments" ("TeamId", "Role")
                 WHERE "TeamId" IS NOT NULL
                   AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS iam."IX_RoleAssignments_OneHolderPerTeamPosition";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                 DROP CONSTRAINT IF EXISTS "CK_RoleAssignment_RoleMatchesScope";
                """);

            // The role normalisation is not reversed: which rows said 'ProjectAdmin' before is not
            // recoverable, and they granted organization read either way.
        }
    }
}
