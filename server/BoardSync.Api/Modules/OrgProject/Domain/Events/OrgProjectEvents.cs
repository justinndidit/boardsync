using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.OrgProject.Domain.Events;

public record OrganizationCreated(
    Guid OrganizationId,
    string Name,
    string Slug,
    Guid CreatedByUserId
) : DomainEvent;

public record ProjectCreated(
    Guid ProjectId,
    Guid OrganizationId,
    string Name,
    string Slug,
    Guid CreatedByUserId
) : DomainEvent;

public record TeamCreated(
    Guid TeamId,
    Guid OrganizationId,
    string Name,
    Guid CreatedByUserId
) : DomainEvent;

public record MemberAddedToOrg(
    Guid OrganizationId,
    Guid UserId,
    Guid AddedByUserId
) : DomainEvent;

public record MemberAddedToTeam(
    Guid TeamId,
    Guid OrganizationId,
    Guid UserId,
    Guid AddedByUserId
) : DomainEvent;

public record MemberRemovedFromTeam(
    Guid TeamId,
    Guid UserId
) : DomainEvent;
