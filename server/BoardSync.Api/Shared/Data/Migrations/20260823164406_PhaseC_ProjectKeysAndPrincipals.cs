using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC_ProjectKeysAndPrincipals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Number",
                schema: "work",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ActorType",
                schema: "work",
                table: "WorkItemHistory",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Every row written before integrations existed was a person's.
                defaultValue: "User");

            migrationBuilder.AddColumn<Guid>(
                name: "AttributedToUserId",
                schema: "work",
                table: "WorkItemHistory",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalType",
                schema: "iam",
                table: "RoleAssignments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Every grant written before integrations existed was a user's.
                defaultValue: "User");

            migrationBuilder.AddColumn<string>(
                name: "Key",
                schema: "org",
                table: "Projects",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NextWorkItemNumber",
                schema: "org",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // ── Backfill, before the unique indexes below can hold ──────────────────
            //
            // Both new columns arrive with a constant default, so every existing row shares one
            // value — and two of them are about to be covered by unique indexes. The order matters:
            // give each row a real value first, then constrain.

            // A key derived from the slug: letters and digits only, upper-cased, first ten. The
            // suffix disambiguates within an organization, since two projects can easily reduce to
            // the same letters. Deterministic, so re-running produces the same answer.
            migrationBuilder.Sql("""
                WITH derived AS (
                    SELECT p."Id",
                           p."OrganizationId",
                           NULLIF(UPPER(LEFT(REGEXP_REPLACE(p."Slug", '[^a-zA-Z0-9]', '', 'g'), 10)), '') AS base
                      FROM org."Projects" p
                ),
                numbered AS (
                    SELECT d."Id",
                           COALESCE(d.base, 'PRJ') AS base,
                           ROW_NUMBER() OVER (
                               PARTITION BY d."OrganizationId", COALESCE(d.base, 'PRJ')
                               ORDER BY d."Id"
                           ) AS ordinal
                      FROM derived d
                )
                UPDATE org."Projects" p
                   SET "Key" = CASE
                                 WHEN n.ordinal = 1 THEN n.base
                                 ELSE LEFT(n.base, GREATEST(1, 10 - LENGTH(n.ordinal::text))) || n.ordinal::text
                               END
                  FROM numbered n
                 WHERE p."Id" = n."Id";
                """);

            // Numbers in creation order, so the oldest item in each project becomes number 1 —
            // which is what anybody reading the list would assume.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT w."Id",
                           ROW_NUMBER() OVER (PARTITION BY w."ProjectId" ORDER BY w."CreatedAt", w."Id") AS n
                      FROM work."WorkItems" w
                )
                UPDATE work."WorkItems" w
                   SET "Number" = numbered.n
                  FROM numbered
                 WHERE w."Id" = numbered."Id";
                """);

            // The counter picks up where the backfill left off, so the next created item does not
            // collide with one that already exists.
            migrationBuilder.Sql("""
                UPDATE org."Projects" p
                   SET "NextWorkItemNumber" = COALESCE(
                       (SELECT MAX(w."Number") + 1 FROM work."WorkItems" w WHERE w."ProjectId" = p."Id"),
                       1);
                """);

            // ── The Integration role becomes assignable at project scope ─────────────
            // Mirrors RolePermissions.IsValidAt. It is never granted to a person: PrincipalType is
            // what separates them, and no endpoint hands this role out.
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
                                                               'Tester', 'Viewer', 'Integration'))
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_Number",
                schema: "work",
                table: "WorkItems",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistory_WorkItemId_FieldName_CreatedAt",
                schema: "work",
                table: "WorkItemHistory",
                columns: new[] { "WorkItemId", "FieldName", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OrganizationId_Key",
                schema: "org",
                table: "Projects",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM iam."RoleAssignments" WHERE "Role" = 'Integration';
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
                                                            'TeamMember', 'Tester', 'Viewer'))
                    OR ("ProjectId" IS NOT NULL AND "Role" IN ('ProjectAdmin', 'Contributor',
                                                               'Tester', 'Viewer'))
                  );
                """);

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_Number",
                schema: "work",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemHistory_WorkItemId_FieldName_CreatedAt",
                schema: "work",
                table: "WorkItemHistory");

            migrationBuilder.DropIndex(
                name: "IX_Projects_OrganizationId_Key",
                schema: "org",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Number",
                schema: "work",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ActorType",
                schema: "work",
                table: "WorkItemHistory");

            migrationBuilder.DropColumn(
                name: "AttributedToUserId",
                schema: "work",
                table: "WorkItemHistory");

            migrationBuilder.DropColumn(
                name: "PrincipalType",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "Key",
                schema: "org",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "NextWorkItemNumber",
                schema: "org",
                table: "Projects");
        }
    }
}
