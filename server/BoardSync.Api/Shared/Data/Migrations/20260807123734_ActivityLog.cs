using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "activity");

            migrationBuilder.CreateTable(
                name: "ActivityLogs",
                schema: "activity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityTitle = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Verb = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OldValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_EntityId",
                schema: "activity",
                table: "ActivityLogs",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_OrganizationId_OccurredAt",
                schema: "activity",
                table: "ActivityLogs",
                columns: new[] { "OrganizationId", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_ProjectId",
                schema: "activity",
                table: "ActivityLogs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_TeamId",
                schema: "activity",
                table: "ActivityLogs",
                column: "TeamId");

            // Backfill from work item history, the only audit trail that existed before this table.
            // Without it every organization's feed would start empty on deploy and the work already
            // recorded would be unreachable. History rows are immutable, so this runs exactly once
            // and needs no de-duplication beyond the empty table it inserts into.
            //
            // A null OldValue means the field was first set rather than changed, which for State is
            // how creation was recorded — those rows become Created, the rest StateChanged/Updated.
            //
            // Backfilled rows are thinner than live ones: history stores the assignee as a raw id
            // rather than a name, and never snapshotted the work item's title, so these carry the
            // title the item has now. Entries recorded from here on resolve both properly.
            migrationBuilder.Sql(@"
                INSERT INTO activity.""ActivityLogs"" (
                    ""Id"", ""OrganizationId"", ""ProjectId"", ""TeamId"", ""ActorId"",
                    ""EntityType"", ""EntityId"", ""EntityTitle"", ""Verb"",
                    ""FieldName"", ""OldValue"", ""NewValue"",
                    ""OccurredAt"", ""CreatedAt"", ""UpdatedAt"", ""CreatedBy"")
                SELECT
                    gen_random_uuid(),
                    p.""OrganizationId"",
                    w.""ProjectId"",
                    w.""TeamId"",
                    h.""ChangedBy"",
                    'WorkItem',
                    h.""WorkItemId"",
                    left(w.""Title"", 255),
                    CASE
                        WHEN h.""FieldName"" = 'State' AND h.""OldValue"" IS NULL THEN 'Created'
                        WHEN h.""FieldName"" = 'State' THEN 'StateChanged'
                        WHEN h.""FieldName"" = 'AssigneeId' THEN 'Assigned'
                        ELSE 'Updated'
                    END,
                    left(h.""FieldName"", 100),
                    left(h.""OldValue"", 1000),
                    left(h.""NewValue"", 1000),
                    h.""CreatedAt"",
                    h.""CreatedAt"",
                    h.""CreatedAt"",
                    h.""ChangedBy""
                FROM work.""WorkItemHistory"" h
                JOIN work.""WorkItems"" w ON w.""Id"" = h.""WorkItemId""
                JOIN org.""Projects"" p ON p.""Id"" = w.""ProjectId"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityLogs",
                schema: "activity");
        }
    }
}
