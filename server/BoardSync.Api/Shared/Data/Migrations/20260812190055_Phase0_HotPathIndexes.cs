using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase0_HotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                schema: "work",
                table: "WorkItemHistory",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Rows written before this column existed default to all-zeros, which would silently
            // exclude every historical entry from the project-filtered notification feed. Copy the
            // project across from the work item each row already points at.
            migrationBuilder.Sql("""
                UPDATE work."WorkItemHistory" AS h
                SET "ProjectId" = w."ProjectId"
                FROM work."WorkItems" AS w
                WHERE h."WorkItemId" = w."Id"
                  AND h."ProjectId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_IsActive_State",
                schema: "work",
                table: "WorkItems",
                columns: new[] { "ProjectId", "IsActive", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistory_ProjectId_CreatedAt",
                schema: "work",
                table: "WorkItemHistory",
                columns: new[] { "ProjectId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_SprintId_Position",
                schema: "plan",
                table: "SprintWorkItems",
                columns: new[] { "SprintId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId_Status",
                schema: "plan",
                table: "Sprints",
                columns: new[] { "TeamId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_IsActive_State",
                schema: "work",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemHistory_ProjectId_CreatedAt",
                schema: "work",
                table: "WorkItemHistory");

            migrationBuilder.DropIndex(
                name: "IX_SprintWorkItems_SprintId_Position",
                schema: "plan",
                table: "SprintWorkItems");

            migrationBuilder.DropIndex(
                name: "IX_Sprints_TeamId_Status",
                schema: "plan",
                table: "Sprints");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "work",
                table: "WorkItemHistory");
        }
    }
}
