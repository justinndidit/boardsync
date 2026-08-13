using BoardSync.Api.Modules.Sprints.Models;
using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.Sprints.DTOs;

/// <summary>Create a new sprint for a team.</summary>
public class CreateSprintRequest
{
    [MaxLength(500)]
    public string? Goal { get; init; }

    [Required]
    public DateTime StartDate { get; init; }

    [Required]
    public DateTime EndDate { get; init; }
}

/// <summary>Update a sprint's goal or dates. Only allowed while Planning.</summary>
public class UpdateSprintRequest
{
    [MaxLength(500)]
    public string? Goal { get; init; }

    [Required]
    public DateTime StartDate { get; init; }

    [Required]
    public DateTime EndDate { get; init; }
}

/// <summary>Transition a sprint to the next status.</summary>
public class UpdateSprintStatusRequest
{
    [Required]
    public SprintStatus Status { get; init; }
}

/// <summary>Add a work item to a sprint backlog.</summary>
public class AddSprintWorkItemRequest
{
    [Required]
    public Guid WorkItemId { get; init; }

    /// <summary>0-based position in the backlog. Omit to append at the end.</summary>
    public int? Position { get; init; }
}

/// <summary>Reorder work items within a sprint backlog.</summary>
public class ReorderSprintWorkItemsRequest
{
    /// <summary>Ordered list of WorkItem IDs representing the desired backlog order.</summary>
    [Required]
    [MinLength(1)]
    public List<Guid> WorkItemIds { get; init; } = new();
}

/// <summary>Move a single work item to a new position between two others (fractional ranking).</summary>
public class MoveSprintWorkItemRequest
{
    /// <summary>Place the item immediately after this work item ID. Null = move to the top.</summary>
    public Guid? AfterWorkItemId { get; init; }

    /// <summary>Place the item immediately before this work item ID. Null = move to the bottom.</summary>
    public Guid? BeforeWorkItemId { get; init; }
}

/// <summary>
/// Options for closing a sprint.
/// Incomplete items (not Resolved or Closed) will be handled according to
/// <see cref="IncompleteItemsDestination"/>.
/// </summary>
public class CloseSprintRequest
{
    /// <summary>
    /// Where to send work items that are not yet Resolved or Closed.
    /// Defaults to ReturnToBacklog.
    /// </summary>
    public IncompleteItemsDestination IncompleteItemsDestination { get; init; }
        = IncompleteItemsDestination.ReturnToBacklog;

    /// <summary>
    /// Required when <see cref="IncompleteItemsDestination"/> is MoveToNextSprint.
    /// </summary>
    public Guid? NextSprintId { get; init; }
}
