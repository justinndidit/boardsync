using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Shared.Auth.DTOs;

// Authentication Responses
public record AuthResponse(
    string AccessToken,
    DateTime ExpiresAt,
    UserProfile User
);

/// <summary>A single role assignment returned inside a user's profile.</summary>
public record UserRoleResponse(
    RoleType Role,
    RoleScope Scope,
    Guid ScopeId
);

public record UserProfile(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string DisplayName,
    string? ProfilePictureUrl,
    bool IsEmailConfirmed,
    bool IsActive,
    DateTime CreatedAt,
    IReadOnlyList<UserRoleResponse>? Roles = null
);

public record TokenResponse(
    string AccessToken,
    DateTime ExpiresAt
);

public record ApiResponse<T>(
    bool Success,
    string Message,
    T? Data = default,
    IDictionary<string, string[]>? Errors = null
);

public record ApiResponse(
    bool Success,
    string Message,
    IDictionary<string, string[]>? Errors = null
) : ApiResponse<object>(Success, Message, null, Errors);

// Error Response
public record ErrorResponse(
    string Message,
    int StatusCode,
    string? Details = null,
    IDictionary<string, string[]>? ValidationErrors = null
);

// Password validation response
public record PasswordValidationResult(
    bool IsValid,
    string[] Errors
);