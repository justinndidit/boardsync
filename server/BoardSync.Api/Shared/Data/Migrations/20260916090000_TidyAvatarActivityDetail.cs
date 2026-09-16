using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Takes the blob URLs out of the avatar entries already in the activity feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The feed renders an entry's detail line as <c>Field: before → after</c>. While the logo was
    /// edited as a pasted URL, those two values were the URLs — so an entry read as roughly 280
    /// characters of two near-identical GUID paths, and the "before" half was a <b>dead link</b>:
    /// committing a new logo deletes the blob it replaced, so the URL it names stopped resolving
    /// the moment the row was written.
    /// </para>
    /// <para>
    /// The services no longer record URLs — they record <c>Logo: updated</c> and
    /// <c>Logo: removed</c> — but that only governs new rows. This rewrites the existing ones into
    /// the same shape, because a feed is read as history and the old entries are the ones anybody
    /// scrolling back actually sees.
    /// </para>
    /// <para>
    /// Which case a row was is recoverable without the URLs: a removal wrote the old URL and left
    /// the new value null, and everything else set a new one.
    /// </para>
    /// <para>
    /// Written by hand. Nothing about the model changes — this edits rows in a column that already
    /// exists — so the scaffolder would produce an empty migration.
    /// </para>
    /// </remarks>
    public partial class TidyAvatarActivityDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /*
             * Scoped to rows that actually hold a URL. An 'Avatar' row without one is already
             * readable and is left alone rather than rewritten on the assumption it is not.
             *
             * 'Logo' rather than 'Avatar' to match what the settings screen calls the field, which
             * is the name the person reading the feed has seen.
             */
            migrationBuilder.Sql("""
                UPDATE activity."ActivityLogs"
                   SET "FieldName" = 'Logo',
                       "NewValue"  = CASE
                                       WHEN "NewValue" LIKE 'http%' THEN 'updated'
                                       ELSE 'removed'
                                     END,
                       "OldValue"  = NULL
                 WHERE "FieldName" = 'Avatar'
                   AND ("OldValue" LIKE 'http%' OR "NewValue" LIKE 'http%');
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Deliberately empty. The URLs are not recoverable — and were not worth recovering: every
        /// one of them named a blob that has already been deleted.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
