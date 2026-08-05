using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Sprints.Models;

/// <summary>
/// A Kanban/Scrum board scoped to a team.
/// One board per team, auto-created on first access with four default columns.
/// </summary>
public class Board : BaseEntity
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "Board";

    // Navigation
    public virtual ICollection<BoardColumn> Columns { get; set; } = new List<BoardColumn>();
}

/// <summary>
/// A column on the Kanban board (e.g. "To Do", "In Progress", "Done").
/// Each column maps to a WorkItemState string so the board query can filter
/// work items into the correct lane without a cross-module enum dependency.
/// </summary>
public class BoardColumn : BaseEntity
{
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The WorkItemState name this column represents ("New", "Active", "Resolved", "Closed").
    /// </summary>
    public string MappedState { get; set; } = string.Empty;

    /// <summary>Left-to-right display order (0-based).</summary>
    public int Position { get; set; }

    /// <summary>Optional WIP limit — null means no limit enforced.</summary>
    public int? WipLimit { get; set; }

    // Navigation
    public virtual Board Board { get; set; } = null!;
}
