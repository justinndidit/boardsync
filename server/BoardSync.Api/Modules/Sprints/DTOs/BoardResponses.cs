using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Sprints.DTOs;

/// <summary>A work item card displayed in a board column.</summary>
public record BoardCardResponse(
    Guid WorkItemId,
    string Title,
    WorkItemType Type,
    WorkItemPriority Priority,
    Guid? AssigneeId,
    int? StoryPoints,
    IReadOnlyList<string> Tags
);

/// <summary>A board column with its current cards.</summary>
public record BoardColumnResponse(
    Guid Id,
    string Name,
    string MappedState,
    int Position,
    int? WipLimit,
    IReadOnlyList<BoardCardResponse> Cards
);

/// <summary>Full board view — all columns with their active-sprint cards.</summary>
/// <remarks>
/// A board belongs to a project. Cards come from the active sprint of the project's
/// assigned team, so <see cref="TeamId"/> is the assigned team the sprint was resolved
/// through — it is not an independent scope.
/// </remarks>
public record BoardResponse(
    Guid Id,
    Guid ProjectId,
    Guid TeamId,
    string Name,
    Guid? ActiveSprintId,
    IReadOnlyList<BoardColumnResponse> Columns,
    DateTime CreatedAt
);

/// <summary>A single column without card data — used by column-management endpoints.</summary>
public record BoardColumnDetailResponse(
    Guid Id,
    Guid BoardId,
    string Name,
    string MappedState,
    int Position,
    int? WipLimit,
    DateTime CreatedAt
);
