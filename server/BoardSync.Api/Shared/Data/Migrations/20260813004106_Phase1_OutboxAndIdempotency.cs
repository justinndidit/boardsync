using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase1_OutboxAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kernel");

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                schema: "activity",
                table: "ActivityLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "kernel",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Sequence);
                });

            // Rows written before the outbox existed all default to the zero GUID, which the unique
            // index below would immediately reject. They were never delivered by an event, so each
            // gets its own synthetic id — that keeps them distinct without pretending they came
            // from an outbox message that never existed.
            migrationBuilder.Sql("""
                UPDATE activity."ActivityLogs"
                SET "EventId" = gen_random_uuid()
                WHERE "EventId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_EventId",
                schema: "activity",
                table: "ActivityLogs",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_EventId",
                schema: "kernel",
                table: "OutboxMessages",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Undispatched",
                schema: "kernel",
                table: "OutboxMessages",
                column: "Sequence",
                filter: "\"DispatchedAt\" IS NULL");

            // Wakes the dispatcher the moment a message is committed, so delivery latency is
            // milliseconds instead of one poll interval. The dispatcher still polls as a fallback:
            // NOTIFY is fire-and-forget and a dropped listener connection must cost latency, never
            // delivery.
            //
            // pg_notify fires from an AFTER trigger, so it only reaches listeners once the
            // transaction commits — a rolled-back write cannot wake anyone to read a row that is
            // no longer there.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION kernel.notify_outbox() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('boardsync_outbox', '');
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER outbox_message_queued
                AFTER INSERT ON kernel."OutboxMessages"
                FOR EACH STATEMENT
                EXECUTE FUNCTION kernel.notify_outbox();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Before the table — dropping it would take the trigger with it but leave the function
            // behind, and re-applying would then fail on CREATE OR REPLACE against a stale body.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS outbox_message_queued ON kernel."OutboxMessages";
                DROP FUNCTION IF EXISTS kernel.notify_outbox();
                """);

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "kernel");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_EventId",
                schema: "activity",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "EventId",
                schema: "activity",
                table: "ActivityLogs");
        }
    }
}
