using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintsAndBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacklogItems",
                schema: "plan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_ProjectId",
                schema: "plan",
                table: "BacklogItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_ProjectId_Rank",
                schema: "plan",
                table: "BacklogItems",
                columns: new[] { "ProjectId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_ProjectId_WorkItemId",
                schema: "plan",
                table: "BacklogItems",
                columns: new[] { "ProjectId", "WorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_SprintId",
                schema: "plan",
                table: "BacklogItems",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "IX_BacklogItems_WorkItemId",
                schema: "plan",
                table: "BacklogItems",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacklogItems",
                schema: "plan");
        }
    }
}
