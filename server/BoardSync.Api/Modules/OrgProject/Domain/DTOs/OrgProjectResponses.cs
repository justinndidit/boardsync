using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.OrgProject.Domain.DTOs;

public record OrganizationResponse(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string? AvatarUrl,
    bool IsActive,
    int MemberCount,
    int ProjectCount,
    DateTime CreatedAt,
    string UserRole
);

public record OrganizationSummaryResponse(
    Guid Id,
    string Slug,
    string Name,
    string? AvatarUrl,
    string Description,
    bool IsActive,
    int MemberCount,
    int ProjectCount,
    DateTime CreatedAt,
    string UserRole
);

/// <remarks>
/// A project has exactly one assigned team (a team may serve several projects), so the
/// project carries the team's identity rather than a count of teams.
/// </remarks>
public record ProjectResponse(
    Guid Id,
    Guid OrganizationId,
    string Slug,
    string Name,
    string Description,
    bool IsActive,
    Guid AssignedTeamId,
    string AssignedTeamName,
    DateTime CreatedAt
);

public record ProjectSummaryResponse(
    Guid Id,
    string Slug,
    string Name
);

public record TeamResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Description,
    bool IsActive,
    int MemberCount,
    DateTime CreatedAt
);

public record TeamMemberResponse(
    Guid UserId,
    string DisplayName,
    string Email,
    string? ProfilePictureUrl,
    DateTime JoinedAt
);

/// <summary>A member of an organization with their org-level role.</summary>
public record OrgMemberResponse(
    Guid UserId,
    string DisplayName,
    string Email,
    string? ProfilePictureUrl,
    string Role,
    DateTime JoinedAt
);

// ---------------------------------------------------------------------------
// Workspace DTOs
// ---------------------------------------------------------------------------

/// <summary>Aggregate counts for the current user's workspace dashboard.</summary>
public record WorkspaceSummaryResponse(
    int Organizations,
    int Projects,
    int Members,
    int ActiveWorkItems
);

/// <summary>A single notification entry for the workspace bell.</summary>
public record WorkspaceNotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Organization,
    DateTime CreatedAt
);

/// <summary>A single recent-activity entry for the workspace feed.</summary>
public record WorkspaceActivityResponse(
    Guid Id,
    string Type,
    string Title,
    string? Detail,
    string ActorName,
    string Organization,
    string Project,
    DateTime OccurredAt
);

// ---------------------------------------------------------------------------
// Search DTOs
// ---------------------------------------------------------------------------

/// <summary>Slim hit returned inside a global search result.</summary>
public record SearchHit(
    Guid Id,
    string Name,
    string? Slug
);

/// <summary>Response envelope for GET /api/search?q=.</summary>
public record GlobalSearchResponse(
    IReadOnlyList<SearchHit> Organizations,
    IReadOnlyList<SearchHit> Projects,
    IReadOnlyList<SearchHit> Members,
    IReadOnlyList<SearchHit> WorkItems
);
