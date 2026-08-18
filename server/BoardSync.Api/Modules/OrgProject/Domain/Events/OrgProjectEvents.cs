using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.OrgProject.Domain.Events;

// Every event here carries OrganizationId, even when the subject is a project or a team. The
// activity log is filtered by organization, and making the raiser supply it keeps the handlers
// from having to walk back up the hierarchy on every single event.

// ── Organization ─────────────────────────────────────────────────────────────

public record OrganizationCreated(
    Guid OrganizationId,
    string Name,
    string Slug,
    Guid CreatedByUserId
) : DomainEvent;

public record OrganizationUpdated(
    Guid OrganizationId,
    string Name,
    string FieldName,
    string? OldValue,
    string? NewValue,
    Guid UpdatedByUserId
) : DomainEvent;

public record MemberAddedToOrg(
    Guid OrganizationId,
    Guid UserId,
    Guid AddedByUserId
) : DomainEvent;

public record MemberRemovedFromOrg(
    Guid OrganizationId,
    Guid UserId,
    Guid RemovedByUserId
) : DomainEvent;

public record OrgMemberRoleChanged(
    Guid OrganizationId,
    Guid UserId,
    RoleType? PreviousRole,
    RoleType NewRole,
    Guid ChangedByUserId
) : DomainEvent;

// ── Project ──────────────────────────────────────────────────────────────────

public record ProjectCreated(
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    string Slug,
    Guid CreatedByUserId
) : DomainEvent;

public record ProjectUpdated(
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    string FieldName,
    string? OldValue,
    string? NewValue,
    Guid UpdatedByUserId
) : DomainEvent;

public record ProjectTeamAssigned(
    Guid ProjectId,
    Guid OrganizationId,
    string ProjectName,
    Guid PreviousTeamId,
    Guid NewTeamId,
    Guid AssignedByUserId
) : DomainEvent;

public record ProjectRoleChanged(
    Guid ProjectId,
    Guid OrganizationId,
    string ProjectName,
    Guid UserId,
    RoleType? NewRole,
    Guid ChangedByUserId
) : DomainEvent;

// ── Team ─────────────────────────────────────────────────────────────────────

public record TeamCreated(
    Guid TeamId,
    Guid OrganizationId,
    string Name,
    Guid CreatedByUserId
) : DomainEvent;

public record TeamUpdated(
    Guid TeamId,
    Guid OrganizationId,
    string Name,
    string FieldName,
    string? OldValue,
    string? NewValue,
    Guid UpdatedByUserId
) : DomainEvent;

public record TeamArchived(
    Guid TeamId,
    Guid OrganizationId,
    string Name,
    Guid ArchivedByUserId
) : DomainEvent;

public record MemberAddedToTeam(
    Guid TeamId,
    Guid OrganizationId,
    Guid UserId,
    Guid AddedByUserId
) : DomainEvent;

public record MemberRemovedFromTeam(
    Guid TeamId,
    Guid OrganizationId,
    Guid UserId,
    Guid RemovedByUserId
) : DomainEvent;

/// <summary>
/// A team position changed hands. <c>FromUserId</c> is null when the position was vacant.
/// </summary>
public record TeamPositionTransferred(
    Guid TeamId,
    Guid OrganizationId,
    RoleType Position,
    Guid? FromUserId,
    Guid ToUserId,
    Guid AssignedByUserId
) : DomainEvent;

/// <summary>
/// A team position was left vacant. Nothing enforces that it is filled — a team can run without a
/// Scrum Master, and blocking a removal to avoid it would be the worse failure — so this exists so
/// somebody can be told.
/// </summary>
public record TeamPositionVacated(
    Guid TeamId,
    Guid OrganizationId,
    RoleType Position,
    Guid PreviousUserId,
    Guid VacatedByUserId
) : DomainEvent;
