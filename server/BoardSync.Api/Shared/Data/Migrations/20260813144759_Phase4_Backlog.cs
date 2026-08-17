using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardSync.Api.Shared.Data.Migrations
{
    /// <summary>
    /// Intentionally empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scaffolded alongside the backlog module, but it never contained the backlog table — that is
    /// created by <c>20260811110255_AddSprintsAndBoards</c>, which is misnamed but correct. What this
    /// one held was a single <c>AddColumn</c> for <c>work.WorkItems.xmin</c>.
    /// </para>
    /// <para>
    /// <c>xmin</c> is Postgres' own system column, mapped in <c>BoardSyncDbContext</c> as a
    /// concurrency token and deliberately backed by no column of ours. Creating it is not merely
    /// redundant — Postgres rejects the name, so applying this migration as written fails and blocks
    /// every migration after it. The scaffolder emits it whenever the model snapshot has drifted;
    /// the snapshot has since been corrected so it stops being suggested.
    /// </para>
    /// <para>
    /// Emptied rather than deleted so the migration history stays stable for anyone who has already
    /// recorded this id.
    /// </para>
    /// </remarks>
    public partial class Phase4_Backlog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
