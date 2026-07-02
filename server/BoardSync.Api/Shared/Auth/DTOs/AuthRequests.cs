using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Shared.Auth.DTOs;

// Authentication Requests
public class LoginRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    public bool RememberMe { get; init; } = false;
}

public class RegisterRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(Password))] 
    public string ConfirmPassword { get; init; } = string.Empty;
    
    [Required] [MaxLength(50)] 
    public string FirstName { get; init; } = string.Empty;
    
    [Required] [MaxLength(50)] 
    public string LastName { get; init; } = string.Empty;
    
    [MaxLength(100)] 
    public string? DisplayName { get; init; }
}

public record ForgotPasswordRequest(
    [Required] [EmailAddress] string Email
);

public class ResetPasswordRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] 
    public string Token { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(Password))] 
    public string ConfirmPassword { get; init; } = string.Empty;
}

public record RefreshTokenRequest(
    [Required] string RefreshToken
);

public record ConfirmEmailRequest(
    [Required] [EmailAddress] string Email,
    [Required] string Token
);

public class ChangePasswordRequest
{
    [Required] 
    public string CurrentPassword { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string NewPassword { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(NewPassword))] 
    public string ConfirmNewPassword { get; init; } = string.Empty;
}

public record UpdateProfileRequest(
    [Required] [MaxLength(50)] string FirstName,
    [Required] [MaxLength(50)] string LastName,
    [MaxLength(100)] string? DisplayName = null,
    [Url] string? ProfilePictureUrl = null
);