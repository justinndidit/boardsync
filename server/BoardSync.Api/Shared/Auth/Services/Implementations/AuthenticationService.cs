using BoardSync.Api.Data;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Models;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

public class AuthenticationService : IAuthenticationService
{
    private readonly BoardSyncDbContext _context;
    private readonly IPasswordService _passwordService;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly JwtSettings _jwtSettings;
    private readonly SecuritySettings _securitySettings;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        BoardSyncDbContext context,
        IPasswordService passwordService,
        ITokenService tokenService,
        IEmailService emailService,
        IOptions<JwtSettings> jwtSettings,
        IOptions<SecuritySettings> securitySettings,
        IOptions<EmailSettings> emailSettings,
        ILogger<AuthenticationService> logger)
    {
        _context = context;
        _passwordService = passwordService;
        _tokenService = tokenService;
        _emailService = emailService;
        _jwtSettings = jwtSettings.Value;
        _securitySettings = securitySettings.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task<ApiResponse<(AuthResponse authResponse, string refreshToken)>> LoginAsync(LoginRequest request, string ipAddress)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                _logger.LogWarning("Login attempt with non-existent email: {Email}", request.Email);
                return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, "Invalid email or password");
            }

            if (user.IsLocked && user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
            {
                var lockTimeRemaining = Math.Max(1, (int)Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes));
                _logger.LogWarning("Login attempt on locked account: {Email}", request.Email);
                return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, $"Account is locked. Try again in {lockTimeRemaining} minutes.");
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("Login attempt on inactive account: {Email}", request.Email);
                return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, "Account is inactive. Please contact support.");
            }

            if (!_passwordService.VerifyPassword(request.Password, user.PasswordHash))
            {
                await HandleFailedLoginAsync(user);
                _logger.LogWarning("Failed login attempt for user: {Email}", request.Email);
                return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, "Invalid email or password");
            }

            if (_securitySettings.RequireEmailConfirmation && !user.IsEmailConfirmed)
            {
                _logger.LogWarning("Login attempt with unconfirmed email: {Email}", request.Email);
                return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, "Please confirm your email before signing in");
            }

            if (user.FailedLoginAttempts > 0 || user.IsLocked)
            {
                user.FailedLoginAttempts = 0;
                user.IsLocked = false;
                user.LockedUntil = null;
            }

            user.LastLoginAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            var accessToken = _tokenService.GenerateAccessToken(user);
            var refreshToken = _tokenService.GenerateRefreshToken();
            var refreshTokenHash = _tokenService.HashToken(refreshToken);
            var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);

            if (!request.RememberMe)
            {
                var oldTokens = await _context.RefreshTokens
                    .Where(rt => rt.UserId == user.Id && rt.IsActive)
                    .ToListAsync();

                foreach (var oldToken in oldTokens)
                {
                    oldToken.Revoked = DateTime.UtcNow;
                    oldToken.RevokedByIp = ipAddress;
                    oldToken.ReasonRevoked = "New login without RememberMe";
                }
            }

            var refreshTokenEntity = new RefreshToken
            {
                Token = refreshTokenHash,
                Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
                UserId = user.Id,
                CreatedByIp = ipAddress
            };

            _context.RefreshTokens.Add(refreshTokenEntity);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User logged in successfully: {Email}", request.Email);

            var userProfile = new UserProfile(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.DisplayName,
                user.ProfilePictureUrl,
                user.IsEmailConfirmed,
                user.CreatedAt
            );

            var authResponse = new AuthResponse(accessToken, expiresAt, userProfile);
            return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(
                true,
                "Login successful",
                (authResponse, refreshToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login for email: {Email}", request.Email);
            return new ApiResponse<(AuthResponse authResponse, string refreshToken)>(false, "An error occurred during login");
        }
    }

    public async Task<ApiResponse> LogoutAsync(Guid userId, string? refreshToken = null)
    {
        try
        {
            var tokensToRevoke = _context.RefreshTokens.Where(rt => rt.UserId == userId && rt.IsActive);

            if (!string.IsNullOrEmpty(refreshToken))
            {
                var refreshTokenHash = _tokenService.HashToken(refreshToken);
                tokensToRevoke = tokensToRevoke.Where(rt => rt.Token == refreshTokenHash);
            }

            var tokens = await tokensToRevoke.ToListAsync();
            foreach (var token in tokens)
            {
                token.Revoked = DateTime.UtcNow;
                token.ReasonRevoked = "User logout";
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("User logged out successfully: {UserId}", userId);
            return new ApiResponse(true, "Logout successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout for user: {UserId}", userId);
            return new ApiResponse(false, "An error occurred during logout");
        }
    }

    public async Task<ApiResponse<(TokenResponse tokenResponse, string refreshToken)>> RefreshTokenAsync(RefreshTokenRequest request, string ipAddress)
    {
        try
        {
            var refreshTokenHash = _tokenService.HashToken(request.RefreshToken);

            var refreshToken = await _context.RefreshTokens
                .Include(rt => rt.User)
                .FirstOrDefaultAsync(rt => rt.Token == refreshTokenHash);

            if (refreshToken == null || !refreshToken.IsActive)
            {
                _logger.LogWarning("Invalid or inactive refresh token used");
                return new ApiResponse<(TokenResponse tokenResponse, string refreshToken)>(false, "Invalid refresh token");
            }

            var user = refreshToken.User;
            if (!user.IsActive)
            {
                _logger.LogWarning("Refresh token used for inactive user: {UserId}", user.Id);
                return new ApiResponse<(TokenResponse tokenResponse, string refreshToken)>(false, "User account is inactive");
            }

            refreshToken.Revoked = DateTime.UtcNow;
            refreshToken.RevokedByIp = ipAddress;
            refreshToken.ReasonRevoked = "Token refreshed";

            var accessToken = _tokenService.GenerateAccessToken(user);
            var newRefreshToken = _tokenService.GenerateRefreshToken();
            var newRefreshTokenHash = _tokenService.HashToken(newRefreshToken);
            var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);

            var newRefreshTokenEntity = new RefreshToken
            {
                Token = newRefreshTokenHash,
                Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
                UserId = user.Id,
                CreatedByIp = ipAddress
            };

            refreshToken.ReplacedByToken = newRefreshTokenHash;
            _context.RefreshTokens.Add(newRefreshTokenEntity);

            await _context.SaveChangesAsync();

            _logger.LogInformation("Token refreshed successfully for user: {UserId}", user.Id);

            var tokenResponse = new TokenResponse(accessToken, expiresAt);
            return new ApiResponse<(TokenResponse tokenResponse, string refreshToken)>(
                true,
                "Token refreshed successfully",
                (tokenResponse, newRefreshToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return new ApiResponse<(TokenResponse tokenResponse, string refreshToken)>(false, "An error occurred while refreshing the token");
        }
    }

    public async Task<ApiResponse> RevokeTokenAsync(string token, string ipAddress, Guid userId)
    {
        try
        {
            var tokenHash = _tokenService.HashToken(token);

            var refreshToken = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.Token == tokenHash && rt.UserId == userId);

            if (refreshToken == null)
            {
                return new ApiResponse(false, "Token not found");
            }

            if (!refreshToken.IsActive)
            {
                return new ApiResponse(false, "Token already revoked");
            }

            refreshToken.Revoked = DateTime.UtcNow;
            refreshToken.RevokedByIp = ipAddress;
            refreshToken.ReasonRevoked = "Manual revocation";

            await _context.SaveChangesAsync();

            _logger.LogInformation("Token revoked successfully for user: {UserId}", userId);
            return new ApiResponse(true, "Token revoked successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking token for user: {UserId}", userId);
            return new ApiResponse(false, "An error occurred while revoking the token");
        }
    }

    public async Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                _logger.LogInformation("Password reset requested for non-existent email: {Email}", request.Email);
                return new ApiResponse(true, "If the email exists, a password reset link has been sent");
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("Password reset requested for inactive account: {Email}", request.Email);
                return new ApiResponse(true, "If the email exists, a password reset link has been sent");
            }

            var resetToken = _tokenService.GeneratePasswordResetToken();
            user.PasswordResetToken = _tokenService.HashToken(resetToken);
            user.PasswordResetTokenExpires = DateTime.UtcNow.AddHours(_securitySettings.PasswordResetTokenExpirationHours);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _emailService.SendPasswordResetAsync(user.Email, resetToken, _emailSettings.BaseUrl);

            _logger.LogInformation("Password reset token generated for user: {Email}", request.Email);
            return new ApiResponse(true, "If the email exists, a password reset link has been sent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing forgot password request: {Email}", request.Email);
            return new ApiResponse(false, "An error occurred while processing the request");
        }
    }

    public async Task<ApiResponse> ResetPasswordAsync(ResetPasswordRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                return new ApiResponse(false, "Invalid reset token");
            }

            var requestTokenHash = _tokenService.HashToken(request.Token);
            if (string.IsNullOrEmpty(user.PasswordResetToken) || user.PasswordResetToken != requestTokenHash)
            {
                return new ApiResponse(false, "Invalid reset token");
            }

            if (user.PasswordResetTokenExpires.HasValue && user.PasswordResetTokenExpires.Value < DateTime.UtcNow)
            {
                return new ApiResponse(false, "Reset token has expired");
            }

            var passwordValidation = _passwordService.ValidatePassword(request.Password);
            if (!passwordValidation.IsValid)
            {
                var errors = new Dictionary<string, string[]>
                {
                    ["Password"] = passwordValidation.Errors
                };
                return new ApiResponse(false, "Password validation failed", errors);
            }

            user.PasswordHash = _passwordService.HashPassword(request.Password);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpires = null;
            user.UpdatedAt = DateTime.UtcNow;

            var refreshTokens = await _context.RefreshTokens.Where(rt => rt.UserId == user.Id && rt.IsActive).ToListAsync();
            foreach (var token in refreshTokens)
            {
                token.Revoked = DateTime.UtcNow;
                token.ReasonRevoked = "Password reset";
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Password reset successfully for user: {Email}", request.Email);
            return new ApiResponse(true, "Password reset successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password: {Email}", request.Email);
            return new ApiResponse(false, "An error occurred while resetting the password");
        }
    }

    private async Task HandleFailedLoginAsync(User user)
    {
        user.FailedLoginAttempts++;
        user.UpdatedAt = DateTime.UtcNow;

        if (user.FailedLoginAttempts >= _securitySettings.MaxFailedAccessAttempts)
        {
            user.IsLocked = true;
            user.LockedUntil = DateTime.UtcNow.AddMinutes(_securitySettings.AccountLockoutMinutes);
            _logger.LogWarning("Account locked due to failed login attempts: {Email}", user.Email);
        }

        await _context.SaveChangesAsync();
    }
}
