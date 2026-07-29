using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_WorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "work");

            migrationBuilder.CreateTable(
                name: "WorkItems",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoryPoints = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItems_WorkItems_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemComments",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    IsEdited = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemComments_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemHistory",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemHistory_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemLinks",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemLinks_WorkItems_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemLinks_WorkItems_TargetId",
                        column: x => x.TargetId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemTags",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemTags_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "work",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_AuthorId",
                schema: "work",
                table: "WorkItemComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_WorkItemId",
                schema: "work",
                table: "WorkItemComments",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistory_ChangedBy",
                schema: "work",
                table: "WorkItemHistory",
                column: "ChangedBy");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistory_WorkItemId",
                schema: "work",
                table: "WorkItemHistory",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemLinks_SourceId_TargetId_LinkType",
                schema: "work",
                table: "WorkItemLinks",
                columns: new[] { "SourceId", "TargetId", "LinkType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemLinks_TargetId",
                schema: "work",
                table: "WorkItemLinks",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_AssigneeId",
                schema: "work",
                table: "WorkItems",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_IsActive",
                schema: "work",
                table: "WorkItems",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ParentId",
                schema: "work",
                table: "WorkItems",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId",
                schema: "work",
                table: "WorkItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_State",
                schema: "work",
                table: "WorkItems",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TeamId",
                schema: "work",
                table: "WorkItems",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Type",
                schema: "work",
                table: "WorkItems",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemTags_Name",
                schema: "work",
                table: "WorkItemTags",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemTags_WorkItemId_Name",
                schema: "work",
                table: "WorkItemTags",
                columns: new[] { "WorkItemId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemComments",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkItemHistory",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkItemLinks",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkItemTags",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkItems",
                schema: "work");
        }
    }
}
