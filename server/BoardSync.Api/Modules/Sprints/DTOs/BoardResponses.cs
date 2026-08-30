using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Sprints.DTOs;

/// <summary>A work item card displayed in a board column.</summary>
/// <remarks>
/// <c>Reference</c> is the work item as people say it — <c>BS-142</c>. On the card because it is
/// what a developer types into a branch name, and branch names are how work binds to git. A board
/// that never shows it makes the whole integration something you have to go and look up somewhere
/// else first.
///
/// <c>Rank</c> is where this card sits in its sprint, as a fractional sort key. Cards arrive
/// ordered by it and a client should keep them that way — it is exposed so a live update can place
/// a card that has changed state into the right position in its new column, rather than appending
/// it and disagreeing with the next read.
///
/// <b>Opaque.</b> Compare it; never compute with it. The values are fractional precisely so a
/// reorder can insert between two neighbours without rewriting every row.
/// </remarks>
public record BoardCardResponse(
    Guid WorkItemId,

    decimal Rank,

    string Reference,
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
