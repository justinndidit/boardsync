namespace BoardSync.Api.Modules.OrgProject.Domain.DTOs;

/// <summary>
/// Read models returned by the OrgProject repositories.
///
/// These exist for list/aggregate queries where returning an entity would force the caller to
/// load navigation collections purely to count them. They are projected in SQL and are not
/// tracked, so they must never be used for mutation — take the entity for that.
/// </summary>

/// <summary>An organization row plus its aggregate counts, for list projections.</summary>
public record OrganizationSummaryRecord(
    Guid Id,
    string Slug,
    string Name,
    string? AvatarUrl,
    string Description,
    bool IsActive,
    int MemberCount,
    int ProjectCount,
    DateTime CreatedAt);

/// <summary>Aggregate counts for a single organization.</summary>
public record OrganizationCounts(int MemberCount, int ProjectCount);

/// <summary>A team row plus its member count.</summary>
public record TeamSummaryRecord(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Description,
    bool IsActive,
    int MemberCount,
    DateTime CreatedAt);

/// <summary>
/// A membership row joined to the member's user profile fields. The join to Users happens in SQL
/// so that member listings can be paged and ordered by display name without loading every row.
/// </summary>
public record MemberRecord(
    Guid UserId,
    string DisplayName,
    string Email,
    string? ProfilePictureUrl,
    DateTime JoinedAt);

