using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.WorkItems.DTOs;

public record WorkItemResponse(
    Guid Id,
    Guid ProjectId,
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
    Guid? CreatedBy
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
