using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Backlog.DTOs;

/// <summary>A single item in the backlog list view.</summary>
/// <summary>One item awaiting a sprint.</summary>
/// <param name="Reference">
/// The work item as people say it — <c>BS-142</c>. On the backlog because this is the list work is
/// picked up from, and the reference is what a branch name has to contain for the board to move
/// itself once somebody starts.
/// </param>
public record BacklogItemResponse(
    Guid BacklogItemId,
    Guid WorkItemId,
    string Reference,
    Guid ProjectId,
    Guid? TeamId,
    Guid? SprintId,
    decimal Rank,
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
