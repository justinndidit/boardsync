using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.OrgProject.DTOs;

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

public record ProjectResponse(
    Guid Id,
    Guid OrganizationId,
    string Slug,
    string Name,
    string Description,
    bool IsActive,
    int TeamCount,
    DateTime CreatedAt
);

public record ProjectSummaryResponse(
    Guid Id,
    string Slug,
    string Name
);

public record TeamResponse(
    Guid Id,
    Guid ProjectId,
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
