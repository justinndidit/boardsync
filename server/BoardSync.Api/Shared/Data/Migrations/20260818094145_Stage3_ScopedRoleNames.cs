using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Gives each scope its own role vocabulary, so no role name means two different things.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Reader</c> was held at all three scopes and meant something different at each: at
    /// organization scope it was what every member is granted on joining — membership, not a
    /// reading permission — while at team and project scope it was genuinely read-only. It splits
    /// into <c>Member</c> and <c>Viewer</c> accordingly. <c>TeamMember</c> at project scope named a
    /// team relationship that project grants do not have, and becomes <c>Contributor</c>; at team
    /// scope, where the name is accurate, it stays.
    /// </para>
    /// <para>
    /// <c>User</c> retires outright. It was assigned by no code path and mapped to no permissions
    /// at any scope, so it is deleted rather than translated — there is nothing to preserve.
    /// </para>
    /// <para>
    /// Written by hand: the model is unchanged (roles are a string column, and the pairing lives in
    /// a raw-SQL check constraint), so the scaffolder produced an empty migration.
    /// </para>
    /// </remarks>
    public partial class Stage3_ScopedRoleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The constraint goes first. It pins Role to the old names, so it would reject every
            // UPDATE below — and re-adding it last is what makes this migration self-checking: if a
            // row were missed, ADD CONSTRAINT fails here and the transaction rolls back, rather than
            // the row surviving to throw inside Enum.Parse on some user's next request.
            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                 DROP CONSTRAINT IF EXISTS "CK_RoleAssignment_RoleMatchesScope";
                """);

            // Defensive, and expected to delete nothing: Stage2_TeamPositions' check constraint
            // already listed no scope where 'User' was legal, so any surviving row would have made
            // that migration fail. Kept because the enum member is being removed here, and a row EF
            // cannot parse takes out every permission check for that user rather than failing
            // locally.
            migrationBuilder.Sql("""
                DELETE FROM iam."RoleAssignments" WHERE "Role" = 'User';
                """);

            // No collision handling needed, unlike the organization normalisation in
            // Stage2_TeamPositions. Every rename here targets a name that did not previously exist,
            // so no row can collide with one already holding the new name under the partial unique
            // indexes on (UserId, Role, <scope column>).
            migrationBuilder.Sql("""
                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Member'
                 WHERE "OrganizationId" IS NOT NULL
                   AND "Role" = 'Reader';

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Viewer'
                 WHERE "TeamId" IS NOT NULL
                   AND "Role" = 'Reader';

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Viewer'
                 WHERE "ProjectId" IS NOT NULL
                   AND "Role" = 'Reader';

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Contributor'
                 WHERE "ProjectId" IS NOT NULL
                   AND "Role" = 'TeamMember';
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                  ADD CONSTRAINT "CK_RoleAssignment_RoleMatchesScope" CHECK (
                       ("OrganizationId" IS NOT NULL AND "Role" IN ('OrgAdmin', 'Member'))
                    OR ("TeamId" IS NOT NULL AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner', 'TeamMember', 'Viewer'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'Contributor', 'Viewer'))
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                 DROP CONSTRAINT IF EXISTS "CK_RoleAssignment_RoleMatchesScope";
                """);

            // The renames reverse exactly — each old name had one new name and vice versa. The
            // deleted 'User' rows do not come back, which costs nothing: they granted nothing.
            migrationBuilder.Sql("""
                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Reader'
                 WHERE "OrganizationId" IS NOT NULL
                   AND "Role" = 'Member';

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'Reader'
                 WHERE ("TeamId" IS NOT NULL OR "ProjectId" IS NOT NULL)
                   AND "Role" = 'Viewer';

                UPDATE iam."RoleAssignments"
                   SET "Role" = 'TeamMember'
                 WHERE "ProjectId" IS NOT NULL
                   AND "Role" = 'Contributor';
                """);

            migrationBuilder.Sql("""
                ALTER TABLE iam."RoleAssignments"
                  ADD CONSTRAINT "CK_RoleAssignment_RoleMatchesScope" CHECK (
                       ("OrganizationId" IS NOT NULL AND "Role" IN ('OrgAdmin', 'Reader'))
                    OR ("TeamId" IS NOT NULL AND "Role" IN ('TeamLead', 'ScrumMaster', 'ProductOwner', 'TeamMember', 'Reader'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'TeamMember', 'Reader'))
                  );
                """);
        }
    }
}
