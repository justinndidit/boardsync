using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Models;

namespace BoardSync.Api.Shared.Auth.Services;

public interface IUserService
{
    Task<ApiResponse<UserProfile>> GetByIdAsync(Guid userId);
    Task<ApiResponse<UserProfile>> GetByEmailAsync(string email);
    Task<ApiResponse<UserProfile>> CreateAsync(RegisterRequest request);
    Task<ApiResponse<UserProfile>> UpdateAsync(Guid userId, UpdateProfileRequest request);
    Task<ApiResponse> DeleteAsync(Guid userId);
    Task<ApiResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request);
    Task<bool> ExistsAsync(string email);
    Task<bool> IsEmailConfirmedAsync(string email);
    Task<ApiResponse> ConfirmEmailAsync(ConfirmEmailRequest request);
    Task<ApiResponse> GenerateEmailConfirmationTokenAsync(string email);
    Task<ApiResponse<string>> GenerateAndSendEmailConfirmationAsync(string email, string baseUrl);
}

public interface IAuthenticationService
{
    Task<ApiResponse<(AuthResponse authResponse, string refreshToken)>> LoginAsync(LoginRequest request, string ipAddress);
    Task<ApiResponse> LogoutAsync(Guid userId, string? refreshToken = null);
    Task<ApiResponse<(TokenResponse tokenResponse, string refreshToken)>> RefreshTokenAsync(RefreshTokenRequest request, string ipAddress);
    Task<ApiResponse> RevokeTokenAsync(string token, string ipAddress, Guid userId);
    Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordRequest request);
    Task<ApiResponse> ResetPasswordAsync(ResetPasswordRequest request);
}

public interface ITokenService
{
    string GenerateAccessToken(User user, Guid? currentWorkspaceId = null);
    string GenerateRefreshToken();
    string GeneratePasswordResetToken();
    string GenerateEmailConfirmationToken();
    string HashToken(string token);
    bool ValidateToken(string token, string secret);
    Guid? GetUserIdFromToken(string token);
}

public interface IPasswordService
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
    PasswordValidationResult ValidatePassword(string password);
}

public interface IEmailService
{
    Task<ApiResponse> SendEmailConfirmationAsync(string email, string token, string baseUrl);
    Task<ApiResponse> SendPasswordResetAsync(string email, string token, string baseUrl);
    Task<ApiResponse> SendWelcomeEmailAsync(string email, string firstName);
    Task<ApiResponse> SendEmailAsync(string to, string subject, string body, bool isHtml = true);
}