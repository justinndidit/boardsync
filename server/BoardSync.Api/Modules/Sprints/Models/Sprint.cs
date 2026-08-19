using BoardSync.Api.Shared.Kernel;
using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Sprints.Models;

/// <summary>
/// Sprint lifecycle state machine.
/// Planning → Active → Completed (transitions are one-way).
/// Only one Active sprint per team is allowed at a time.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprintStatus
{
    Planning,
    Active,
    Completed
}

/// <summary>
/// A time-boxed iteration scoped to a project.
/// </summary>
public class Sprint : BaseEntity
{
    /// <summary>The project this sprint belongs to.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Auto-incremented sprint number within the project (Sprint 1, Sprint 2 …).</summary>
    public int Number { get; set; }

    /// <summary>Optional sprint goal / focus statement shown on the board header.</summary>
    public string? Goal { get; set; }

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public SprintStatus Status { get; set; } = SprintStatus.Planning;

    // Navigation
    public virtual ICollection<SprintWorkItem> SprintWorkItems { get; set; } = new List<SprintWorkItem>();
}

/// <summary>
/// Join table — assigns a work item to a sprint with an optional display order.
/// </summary>
public class SprintWorkItem : BaseEntity
{
    public Guid SprintId { get; set; }
    public Guid WorkItemId { get; set; }

    /// <summary>Display position within the sprint backlog (0-based).</summary>
    /// <remarks>
    /// Superseded by <see cref="Rank"/> for ordering; kept so the existing whole-list reorder
    /// endpoint and any client reading it keep working. Written alongside Rank, never read for
    /// sort order.
    /// </remarks>
    public int Position { get; set; }

    /// <summary>
    /// Fractional sort key. Ordering is by this, ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fractional so that moving a card is a single-row update: to place it between two neighbours,
    /// take the midpoint of their ranks. Nothing else in the backlog is touched.
    /// </para>
    /// <para>
    /// That property is what makes concurrent editing safe. Rewriting every row's integer position
    /// meant two people dragging different cards each wrote back a complete ordering computed
    /// before the other's move existed, so whoever saved second silently reverted the first. With
    /// ranks, two people moving different cards touch different rows and cannot collide at all;
    /// two people moving the *same* card resolve as last-write-wins on one row, which is both
    /// correct and what users expect.
    /// </para>
    /// <para>
    /// <c>numeric</c> rather than a float: repeated midpoints need exact arithmetic, and binary
    /// floating point runs out of precision after about 50 subdivisions of the same gap. A
    /// rebalance is still needed eventually, just far less often.
    /// </para>
    /// </remarks>
    public decimal Rank { get; set; }

    // Navigation
    public virtual Sprint Sprint { get; set; } = null!;
}
