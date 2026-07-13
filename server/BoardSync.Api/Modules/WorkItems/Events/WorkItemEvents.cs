using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.WorkItems.Events;

public record WorkItemCreated(
    Guid WorkItemId,
    Guid ProjectId,
    WorkItemType Type,
    string Title,
    Guid CreatedByUserId
) : DomainEvent;

public record WorkItemStateChanged(
    Guid WorkItemId,
    Guid ProjectId,
    WorkItemState OldState,
    WorkItemState NewState,
    Guid ChangedByUserId
) : DomainEvent;

public record WorkItemAssigned(
    Guid WorkItemId,
    Guid ProjectId,
    Guid? PreviousAssigneeId,
    Guid? NewAssigneeId,
    Guid ChangedByUserId
) : DomainEvent;

public record WorkItemUpdated(
    Guid WorkItemId,
    Guid ProjectId,
    string FieldName,
    string? OldValue,
    string? NewValue,
    Guid ChangedByUserId
) : DomainEvent;

public record WorkItemDeleted(
    Guid WorkItemId,
    Guid ProjectId,
    Guid DeletedByUserId
) : DomainEvent;

public record WorkItemCommentAdded(
    Guid CommentId,
    Guid WorkItemId,
    Guid ProjectId,
    Guid AuthorId
) : DomainEvent;

public record WorkItemLinked(
    Guid SourceId,
    Guid TargetId,
    WorkItemLinkType LinkType,
    Guid LinkedByUserId
) : DomainEvent;
