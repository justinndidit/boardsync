using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.Sprints.Events;

// Sprints and boards both hang off a project. Both carry OrganizationId for the same reason the
// OrgProject events do — the activity log filters on it.

public record SprintCreated(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    Guid CreatedByUserId
) : DomainEvent;

public record SprintUpdated(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    string FieldName,
    string? OldValue,
    string? NewValue,
    Guid UpdatedByUserId
) : DomainEvent;

public record SprintStatusChanged(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    SprintStatus OldStatus,
    SprintStatus NewStatus,
    Guid ChangedByUserId
) : DomainEvent;

public record SprintDeleted(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    Guid DeletedByUserId
) : DomainEvent;

public record SprintWorkItemAdded(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string SprintName,
    Guid WorkItemId,
    string WorkItemTitle,
    Guid AddedByUserId
) : DomainEvent;

public record SprintWorkItemRemoved(
    Guid SprintId,
    Guid ProjectId,
    Guid OrganizationId,
    string SprintName,
    Guid WorkItemId,
    string WorkItemTitle,
    Guid RemovedByUserId
) : DomainEvent;

public record WorkItemRankChange(Guid WorkItemId, decimal Rank);

public record WorkItemMoved(
    Guid WorkItemId,
    Guid ProjectId,
    Guid SprintId,
    WorkItemState State,
    decimal Rank,
    long Version,
    Guid ChangedByUserId,
    IReadOnlyList<WorkItemRankChange> RankChanges
) : DomainEvent;

/// <summary>
/// Any change to a board or one of its columns. Boards are small enough that a single event with
/// a <paramref name="Change"/> label beats one event type per column operation.
/// </summary>
public record BoardChanged(
    Guid BoardId,
    Guid ProjectId,
    Guid OrganizationId,
    string BoardName,
    string Change,
    string? OldValue,
    string? NewValue,
    Guid ChangedByUserId
) : DomainEvent;
