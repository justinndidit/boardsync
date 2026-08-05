using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenRoleAssignmentAndOrgMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Projects_ProjectId",
                schema: "org",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_Scope_ScopeId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_UserId_Role_Scope_ScopeId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "ScopeId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                schema: "org",
                table: "Teams",
                newName: "OrganizationId");

            migrationBuilder.RenameIndex(
                name: "IX_Teams_ProjectId_Name",
                schema: "org",
                table: "Teams",
                newName: "IX_Teams_OrganizationId_Name");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "iam",
                table: "RoleAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                schema: "iam",
                table: "RoleAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                schema: "iam",
                table: "RoleAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedTeamId",
                schema: "org",
                table: "Projects",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_OrganizationId",
                schema: "iam",
                table: "RoleAssignments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_ProjectId",
                schema: "iam",
                table: "RoleAssignments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_Scope_ProjectId_TeamId_OrganizationId",
                schema: "iam",
                table: "RoleAssignments",
                columns: new[] { "Scope", "ProjectId", "TeamId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_TeamId",
                schema: "iam",
                table: "RoleAssignments",
                column: "TeamId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoleAssignment_ExactlyOneScope",
                schema: "iam",
                table: "RoleAssignments",
                sql: "(CASE WHEN \"OrganizationId\" IS NOT NULL THEN 1 ELSE 0 END +\r\n                CASE WHEN \"ProjectId\" IS NOT NULL THEN 1 ELSE 0 END +\r\n                CASE WHEN \"TeamId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ""IX_RoleAssignments_Unique_Org""
                ON iam.""RoleAssignments"" (""UserId"", ""Role"", ""OrganizationId"")
                WHERE ""OrganizationId"" IS NOT NULL;

                CREATE UNIQUE INDEX ""IX_RoleAssignments_Unique_Project""
                ON iam.""RoleAssignments"" (""UserId"", ""Role"", ""ProjectId"")
                WHERE ""ProjectId"" IS NOT NULL;

                CREATE UNIQUE INDEX ""IX_RoleAssignments_Unique_Team""
                ON iam.""RoleAssignments"" (""UserId"", ""Role"", ""TeamId"")
                WHERE ""TeamId"" IS NOT NULL;
            ");
                        migrationBuilder.CreateIndex(
                name: "IX_Projects_AssignedTeamId",
                schema: "org",
                table: "Projects",
                column: "AssignedTeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Teams_AssignedTeamId",
                schema: "org",
                table: "Projects",
                column: "AssignedTeamId",
                principalSchema: "org",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RoleAssignments_Organizations_OrganizationId",
                schema: "iam",
                table: "RoleAssignments",
                column: "OrganizationId",
                principalSchema: "org",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RoleAssignments_Projects_ProjectId",
                schema: "iam",
                table: "RoleAssignments",
                column: "ProjectId",
                principalSchema: "org",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RoleAssignments_Teams_TeamId",
                schema: "iam",
                table: "RoleAssignments",
                column: "TeamId",
                principalSchema: "org",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Organizations_OrganizationId",
                schema: "org",
                table: "Teams",
                column: "OrganizationId",
                principalSchema: "org",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Teams_AssignedTeamId",
                schema: "org",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_RoleAssignments_Organizations_OrganizationId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_RoleAssignments_Projects_ProjectId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_RoleAssignments_Teams_TeamId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Organizations_OrganizationId",
                schema: "org",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_OrganizationId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_ProjectId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_Scope_ProjectId_TeamId_OrganizationId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_TeamId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RoleAssignment_ExactlyOneScope",
                schema: "iam",
                table: "RoleAssignments");
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS iam.""IX_RoleAssignments_Unique_Org"";
                DROP INDEX IF EXISTS iam.""IX_RoleAssignments_Unique_Project"";
                DROP INDEX IF EXISTS iam.""IX_RoleAssignments_Unique_Team"";
");

            migrationBuilder.DropIndex(
                name: "IX_Projects_AssignedTeamId",
                schema: "org",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "TeamId",
                schema: "iam",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "AssignedTeamId",
                schema: "org",
                table: "Projects");

            migrationBuilder.RenameColumn(
                name: "OrganizationId",
                schema: "org",
                table: "Teams",
                newName: "ProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Teams_OrganizationId_Name",
                schema: "org",
                table: "Teams",
                newName: "IX_Teams_ProjectId_Name");

            migrationBuilder.AddColumn<Guid>(
                name: "ScopeId",
                schema: "iam",
                table: "RoleAssignments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_Scope_ScopeId",
                schema: "iam",
                table: "RoleAssignments",
                columns: new[] { "Scope", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserId_Role_Scope_ScopeId",
                schema: "iam",
                table: "RoleAssignments",
                columns: new[] { "UserId", "Role", "Scope", "ScopeId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Projects_ProjectId",
                schema: "org",
                table: "Teams",
                column: "ProjectId",
                principalSchema: "org",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
