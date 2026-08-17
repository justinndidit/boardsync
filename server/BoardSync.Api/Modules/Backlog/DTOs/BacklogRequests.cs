using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.Backlog.DTOs;

/// <summary>Add a work item to the project backlog.</summary>
public class AddToBacklogRequest
{
    [Required]
    public Guid WorkItemId { get; init; }

    /// <summary>Optional team scope for this backlog entry.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>Desired rank position (0-based). Omit to append at the bottom.</summary>
    public int? Rank { get; init; }
}

/// <summary>Reorder backlog items by providing the new rank-ordered list of work item IDs.</summary>
public class ReorderBacklogRequest
{
    /// <summary>Work item IDs in desired rank order (index 0 = highest priority).</summary>
    [Required]
    [MinLength(1)]
    public List<Guid> WorkItemIds { get; init; } = [];
}

/// <summary>Move one or more backlog items into a sprint.</summary>
public class MoveToSprintRequest
{
    [Required]
    [MinLength(1)]
    public List<Guid> WorkItemIds { get; init; } = [];

    [Required]
    public Guid SprintId { get; init; }
}

/// <summary>Return one or more sprint items back to the backlog.</summary>
public class ReturnToBacklogRequest
{
    [Required]
    [MinLength(1)]
    public List<Guid> WorkItemIds { get; init; } = [];
}
