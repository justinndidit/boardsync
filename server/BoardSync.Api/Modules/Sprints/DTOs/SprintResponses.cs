using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Sprints.DTOs;

/// <summary>Where incomplete sprint items are sent on close-out.</summary>
public enum IncompleteItemsDestination
{
    /// <summary>Items are cleared from the sprint and returned to the project backlog.</summary>
    ReturnToBacklog,

    /// <summary>Items are moved into the specified next sprint.</summary>
    MoveToNextSprint
}

/// <summary>Full sprint detail including velocity metrics.</summary>
public record SprintResponse(
    Guid Id,
    Guid ProjectId,
    int Number,
    string? Goal,
    DateTime StartDate,
    DateTime EndDate,
    SprintStatus Status,
    int WorkItemCount,
    int CompletedCount,
    int TotalStoryPoints,
    int CompletedStoryPoints,
    DateTime CreatedAt
);

/// <summary>Lightweight sprint summary for list views.</summary>
public record SprintSummaryResponse(
    Guid Id,
    int Number,
    string? Goal,
    DateTime StartDate,
    DateTime EndDate,
    SprintStatus Status,
    int WorkItemCount
);

/// <summary>A single work item entry within a sprint backlog.</summary>
public record SprintWorkItemResponse(
    Guid WorkItemId,

    /// <summary>
    /// The work item as people say it — <c>BS-142</c>. What a developer puts in a branch name.
    /// </summary>
    string Reference,

    string Title,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    Guid? AssigneeId,
    int? StoryPoints,
    int Position
);

/// <summary>Where a moved backlog item landed.</summary>
/// <param name="WorkItemId">The item that moved.</param>
/// <param name="Rank">
/// Its new sort key. Ordering is by this ascending; treat the value as opaque and only compare it.
/// </param>
public record MoveSprintWorkItemResponse(Guid WorkItemId, decimal Rank);

public record MoveWorkItemCommandResponse(
    Guid WorkItemId,
    WorkItemState State,
    decimal Rank,
    long Version);

/// <summary>Summary returned after closing a sprint.</summary>
public record CloseSprintResponse(
    SprintResponse Sprint,
    int CompletedItemCount,
    int IncompleteItemCount,
    IncompleteItemsDestination Destination,
    Guid? NextSprintId
);
