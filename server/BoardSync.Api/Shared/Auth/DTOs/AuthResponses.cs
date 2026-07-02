namespace BoardSync.Api.Shared.Auth.DTOs;

// Authentication Responses
public record AuthResponse(
    string AccessToken,
    DateTime ExpiresAt,
    UserProfile User
);

public record UserProfile(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string DisplayName,
    string? ProfilePictureUrl,
    bool IsEmailConfirmed,
    DateTime CreatedAt
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