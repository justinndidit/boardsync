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
/// A time-boxed iteration scoped to a team.
/// </summary>
public class Sprint : BaseEntity
{
    /// <summary>The team this sprint belongs to.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Auto-incremented sprint number within the team (Sprint 1, Sprint 2 …).</summary>
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
    public int Position { get; set; }

    // Navigation
    public virtual Sprint Sprint { get; set; } = null!;
}
