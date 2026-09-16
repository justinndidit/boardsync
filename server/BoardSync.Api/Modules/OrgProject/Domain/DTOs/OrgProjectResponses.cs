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
    string Key,
    string Name,
    string Description,
    bool IsActive,
    Guid AssignedTeamId,
    string AssignedTeamName,
    bool AllowSelfCertification,
    DateTime CreatedAt
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

/// <summary>An invitation as an administrator sees it in the members screen.</summary>
/// <param name="Id">The invitation, for revoking it.</param>
/// <param name="Email">Who was invited.</param>
/// <param name="Role">What they get on acceptance.</param>
/// <param name="Status">
/// <c>Pending</c>, <c>Accepted</c>, <c>Revoked</c> or <c>Expired</c> — derived, never stored, so it
/// cannot disagree with the timestamps it is derived from.
/// </param>
/// <param name="InvitedBy">Display name of the administrator who sent it, when still resolvable.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
/// <param name="CreatedAt">When it was sent.</param>
public record OrganizationInvitationResponse(
    Guid Id,
    string Email,
    string Role,
    string Status,
    string? InvitedBy,
    DateTime ExpiresAt,
    DateTime CreatedAt
);

/// <summary>
/// What somebody holding an invitation link is shown before they accept.
/// </summary>
/// <remarks>
/// Deliberately thin, and reachable without signing in — it has to be, because the recipient may
/// have no account yet and the page has to decide whether to send them to sign-in or to sign-up.
/// It carries the organization's name and the invited address and nothing else about either side:
/// a link that leaks into the wrong hands should not become a readout of an organization's
/// membership or of who else was invited.
/// </remarks>
/// <param name="OrganizationName">Which organization, so the page can say what is being joined.</param>
/// <param name="Email">The address invited. The account that accepts must own it.</param>
/// <param name="Role">What the invitation grants.</param>
/// <param name="InvitedBy">Who sent it, when still resolvable.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
/// <param name="HasAccount">
/// Whether an account already exists for <paramref name="Email"/>, so the page can offer "sign in"
/// rather than "create an account".
/// </param>
public record InvitationPreviewResponse(
    string OrganizationName,
    string Email,
    string Role,
    string? InvitedBy,
    DateTime ExpiresAt,
    bool HasAccount
);

/// <summary>Where a caller lands after accepting.</summary>
/// <param name="OrganizationId">The organization joined.</param>
/// <param name="OrganizationSlug">Its slug, so the client can route straight into it.</param>
/// <param name="OrganizationName">Its name, for the confirmation.</param>
public record InvitationAcceptedResponse(
    Guid OrganizationId,
    string OrganizationSlug,
    string OrganizationName
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

/// <summary>
/// One of a team's positions and who holds it. <c>UserId</c> is null when the position is vacant,
/// which is a legitimate state rather than an error.
/// </summary>
public record TeamPositionResponse(
    RoleType Position,
    Guid? UserId
);

/// <summary>An ordinary team-scope role assignment.</summary>
/// <remarks>
/// Not a position. <c>TeamPositionResponse</c> covers Team Lead, Scrum Master and Product Owner,
/// which are single seats handed over rather than granted — this is everything else, and in
/// practice it is how somebody becomes the team's <c>Tester</c>.
/// </remarks>
/// <param name="UserId">Who holds it.</param>
/// <param name="Role">What they hold.</param>
/// <param name="AssignedAt">When it was granted.</param>
public record TeamRoleResponse(
    Guid UserId,
    Rbac.Models.RoleType Role,
    DateTime AssignedAt);

/// <summary>Request body for granting a team-scope role.</summary>
public record AssignTeamRoleRequest(
    Guid UserId,
    Rbac.Models.RoleType Role);
