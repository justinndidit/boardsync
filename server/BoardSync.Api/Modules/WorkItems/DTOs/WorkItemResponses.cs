using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.WorkItems.DTOs;

/// <remarks>
/// <para>
/// <c>Reference</c> is what people type — <c>BS-142</c> — assembled from the project's key and this
/// item's number. Show it wherever an item is identified; it is the only form a developer can put in
/// a branch name, and the whole git integration keys on it.
/// </para>
/// <para>
/// <c>Version</c> is an opaque row version. Send it back as <c>expectedVersion</c> when updating to
/// be told about a conflict instead of silently overwriting whoever edited in between. Compare only
/// — never compute with it or assume it increments by one.
/// </para>
/// <para>
/// It deliberately has no default value. It used to default to <c>0</c>, and the one place that
/// builds this record never passed it — so every response advertised version 0, every
/// <c>expectedVersion</c> a client echoed back was 0, and the conflict check could never match.
/// Without a default, forgetting it is a compile error rather than a feature that quietly does
/// nothing.
/// </para>
/// </remarks>
public record WorkItemResponse(
    Guid Id,
    Guid ProjectId,
    int Number,
    string Reference,
    Guid? TeamId,
    Guid? ParentId,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    string Title,
    string? Description,
    Guid? AssigneeId,
    int? StoryPoints,
    List<string> Tags,
    int CommentCount,
    int ChildCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy,
    long Version
);

public record WorkItemSummaryResponse(
    Guid Id,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    string Title,
    Guid? AssigneeId,
    int? StoryPoints,
    List<string> Tags,
    int ChildCount,
    DateTime CreatedAt
);

public record WorkItemCommentResponse(
    Guid Id,
    Guid WorkItemId,
    Guid AuthorId,
    string Body,
    bool IsEdited,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record WorkItemHistoryResponse(
    Guid Id,
    Guid WorkItemId,
    Guid ChangedBy,
    string FieldName,
    string? OldValue,
    string? NewValue,
    DateTime CreatedAt
);

public record WorkItemLinkResponse(
    Guid Id,
    Guid SourceId,
    Guid TargetId,
    WorkItemLinkType LinkType,
    string TargetTitle,
    WorkItemType TargetType,
    WorkItemState TargetState
);
