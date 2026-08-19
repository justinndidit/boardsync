using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TeamId",
                schema: "plan",
                table: "Sprints",
                newName: "ProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_TeamId_Status",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_ProjectId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_TeamId_Number",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_ProjectId_Number");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_TeamId",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProjectId",
                schema: "plan",
                table: "Sprints",
                newName: "TeamId");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_ProjectId_Status",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_TeamId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_ProjectId_Number",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_TeamId_Number");

            migrationBuilder.RenameIndex(
                name: "IX_Sprints_ProjectId",
                schema: "plan",
                table: "Sprints",
                newName: "IX_Sprints_TeamId");
        }
    }
}
