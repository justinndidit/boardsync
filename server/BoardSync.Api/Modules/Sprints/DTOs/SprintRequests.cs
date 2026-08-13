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
/// <summary>
/// Moves one backlog item to sit between two others.
/// </summary>
/// <remarks>
/// Prefer this over <see cref="ReorderSprintWorkItemsRequest"/> for drag-and-drop. It names only
/// the card that moved and where it landed, so two people dragging different cards touch different
/// rows and cannot overwrite each other. Sending a whole ordering — which is what the older
/// endpoint takes — means submitting a view of the list computed before the other person's move
/// existed, and silently reverting it.
/// </remarks>
public class MoveSprintWorkItemRequest
{
    /// <summary>
    /// The item the moved card should sit *after*, or null when it moves to the top.
    /// </summary>
    public Guid? AfterWorkItemId { get; init; }

    /// <summary>
    /// The item the moved card should sit *before*, or null when it moves to the end.
    /// </summary>
    public Guid? BeforeWorkItemId { get; init; }
}

public class ReorderSprintWorkItemsRequest
{
    /// <summary>Ordered list of WorkItem IDs representing the desired backlog order.</summary>
    [Required]
    [MinLength(1)]
    public List<Guid> WorkItemIds { get; init; } = new();
}
