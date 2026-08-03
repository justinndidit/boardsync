using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_SprintsBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plan");

            migrationBuilder.CreateTable(
                name: "Boards",
                schema: "plan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sprints",
                schema: "plan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sprints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BoardColumns",
                schema: "plan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MappedState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    WipLimit = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardColumns_Boards_BoardId",
                        column: x => x.BoardId,
                        principalSchema: "plan",
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SprintWorkItems",
                schema: "plan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SprintWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SprintWorkItems_Sprints_SprintId",
                        column: x => x.SprintId,
                        principalSchema: "plan",
                        principalTable: "Sprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardColumns_BoardId",
                schema: "plan",
                table: "BoardColumns",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Boards_TeamId",
                schema: "plan",
                table: "Boards",
                column: "TeamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_Status",
                schema: "plan",
                table: "Sprints",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId",
                schema: "plan",
                table: "Sprints",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_TeamId_Number",
                schema: "plan",
                table: "Sprints",
                columns: new[] { "TeamId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_SprintId_WorkItemId",
                schema: "plan",
                table: "SprintWorkItems",
                columns: new[] { "SprintId", "WorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SprintWorkItems_WorkItemId",
                schema: "plan",
                table: "SprintWorkItems",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardColumns",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "SprintWorkItems",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "Boards",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "Sprints",
                schema: "plan");
        }
    }
}
