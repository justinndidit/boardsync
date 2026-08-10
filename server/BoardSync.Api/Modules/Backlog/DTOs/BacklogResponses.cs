using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Backlog.DTOs;

/// <summary>A single item in the backlog list view.</summary>
public record BacklogItemResponse(
    Guid BacklogItemId,
    Guid WorkItemId,
    Guid ProjectId,
    Guid? TeamId,
    Guid? SprintId,
    int Rank,
    string Title,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    Guid? AssigneeId,
    int? StoryPoints,
    IReadOnlyList<string> Tags,
    int ChildCount,
    DateTime CreatedAt
);

/// <summary>Result of a move-to-sprint or return-to-backlog bulk operation.</summary>
public record BacklogBulkOperationResponse(
    int Affected,
    string Message
);
