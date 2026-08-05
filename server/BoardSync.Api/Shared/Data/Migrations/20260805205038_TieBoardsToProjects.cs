using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class TieBoardsToProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TeamId",
                schema: "plan",
                table: "Boards",
                newName: "ProjectId");

            migrationBuilder.RenameIndex(
                name: "IX_Boards_TeamId",
                schema: "plan",
                table: "Boards",
                newName: "IX_Boards_ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Boards_Projects_ProjectId",
                schema: "plan",
                table: "Boards",
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
                name: "FK_Boards_Projects_ProjectId",
                schema: "plan",
                table: "Boards");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                schema: "plan",
                table: "Boards",
                newName: "TeamId");

            migrationBuilder.RenameIndex(
                name: "IX_Boards_ProjectId",
                schema: "plan",
                table: "Boards",
                newName: "IX_Boards_TeamId");
        }
    }
}
