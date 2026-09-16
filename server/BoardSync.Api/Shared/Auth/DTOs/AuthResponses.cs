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
    Guid? OrganizationId,
    Guid? ProjectId,
    Guid? TeamId
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

/// <summary>A signed URL the browser may upload one profile picture to, and how to use it.</summary>
/// <param name="BlobPath">Hand this back to <c>POST /Auth/profile/picture</c> once the upload finishes.</param>
/// <param name="UploadUrl">PUT the file here. Carries its own credential; no bearer token belongs on it.</param>
/// <param name="Headers">Headers the PUT must send. The storage service rejects it without them.</param>
/// <param name="ExpiresAt">When <paramref name="UploadUrl"/> stops working.</param>
/// <param name="MaxBytes">The size ceiling the server will accept on commit, so the client can say so first.</param>
public record AvatarUploadTicketResponse(
    string BlobPath,
    string UploadUrl,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt,
    long MaxBytes
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