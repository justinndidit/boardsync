using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Sprints.DTOs;

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
