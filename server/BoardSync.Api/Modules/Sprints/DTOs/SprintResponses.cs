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
    Guid TeamId,
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
    string Title,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    Guid? AssigneeId,
    int? StoryPoints,
    int Position
);

/// <summary>Summary returned after closing a sprint.</summary>
public record CloseSprintResponse(
    SprintResponse Sprint,
    int CompletedItemCount,
    int IncompleteItemCount,
    IncompleteItemsDestination Destination,
    Guid? NextSprintId
);
