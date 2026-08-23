using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Fills in <c>WorkItemHistory.ProjectId</c>, which was never written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The column, its migration and the <c>(ProjectId, CreatedAt)</c> index all shipped; the write
    /// did not. <c>WorkItemService.AddHistory</c> only ever received a work item id, so every row
    /// ever written carries <c>uuid_nil</c>. The notification feed filters on exactly this column,
    /// which is why it returned nothing to anybody — including to users who could see everything.
    /// </para>
    /// <para>
    /// No schema change: the write is fixed in <c>AddHistory</c>, and this repairs the rows already
    /// stored. Work items never move between projects, so the value recovered from the work item is
    /// the value that should always have been here.
    /// </para>
    /// </remarks>
    public partial class BackfillWorkItemHistoryProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Batched, and outside the migration transaction, so a large history table is repaired
            // without holding a write lock on it for the length of one enormous UPDATE. Each batch
            // commits on its own; the WHERE clause is the progress marker, so an interrupted run
            // simply resumes where it stopped and re-running is a no-op.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    updated integer;
                BEGIN
                    LOOP
                        UPDATE work."WorkItemHistory" AS h
                           SET "ProjectId" = w."ProjectId"
                          FROM work."WorkItems" AS w
                         WHERE h."WorkItemId" = w."Id"
                           AND h."ProjectId" = '00000000-0000-0000-0000-000000000000'
                           AND h."Id" IN (
                               SELECT "Id" FROM work."WorkItemHistory"
                                WHERE "ProjectId" = '00000000-0000-0000-0000-000000000000'
                                LIMIT 10000
                           );

                        GET DIAGNOSTICS updated = ROW_COUNT;
                        EXIT WHEN updated = 0;
                        COMMIT;
                    END LOOP;
                END $$;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. This migration repairs data rather than changing shape, and
            // reversing it would mean writing uuid_nil back over correct values — reintroducing the
            // defect and losing the only copy of what the right answer was.
        }
    }
}
