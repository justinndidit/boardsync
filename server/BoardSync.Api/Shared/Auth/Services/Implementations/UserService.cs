using BoardSync.Api.Data;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Models;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

public class UserService : IUserService
{
    private readonly BoardSyncDbContext _context;
    private readonly IPasswordService _passwordService;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly SecuritySettings _securitySettings;
    private readonly ILogger<UserService> _logger;

    public UserService(
        BoardSyncDbContext context,
        IPasswordService passwordService,
        ITokenService tokenService,
        IEmailService emailService,
        IOptions<SecuritySettings> securitySettings,
        ILogger<UserService> logger)
    {
        _context = context;
        _passwordService = passwordService;
        _tokenService = tokenService;
        _emailService = emailService;
        _securitySettings = securitySettings.Value;
        _logger = logger;
    }

    public async Task<ApiResponse<UserProfile>> GetByIdAsync(Guid userId)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return new ApiResponse<UserProfile>(false, "User not found");
            }

            var userProfile = MapToUserProfile(user);
            return new ApiResponse<UserProfile>(true, "User found", userProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user by ID: {UserId}", userId);
            return new ApiResponse<UserProfile>(false, "An error occurred while retrieving the user");
        }
    }

    public async Task<ApiResponse<UserProfile>> GetByEmailAsync(string email)
    {
        try
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                return new ApiResponse<UserProfile>(false, "User not found");
            }

            var userProfile = MapToUserProfile(user);
            return new ApiResponse<UserProfile>(true, "User found", userProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user by email: {Email}", email);
            return new ApiResponse<UserProfile>(false, "An error occurred while retrieving the user");
        }
    }

    public async Task<ApiResponse<UserProfile>> CreateAsync(RegisterRequest request)
    {
        try
        {
            if (await ExistsAsync(request.Email))
            {
                return new ApiResponse<UserProfile>(false, "User with this email already exists");
            }

            var passwordValidation = _passwordService.ValidatePassword(request.Password);
            if (!passwordValidation.IsValid)
            {
                var errors = new Dictionary<string, string[]>
                {
                    ["Password"] = passwordValidation.Errors
                };
                return new ApiResponse<UserProfile>(false, "Password validation failed", null, errors);
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = request.Email.Trim().ToLowerInvariant(),
                FirstName = request.FirstName,
                LastName = request.LastName,
                DisplayName = request.DisplayName ?? $"{request.FirstName} {request.LastName}",
                PasswordHash = _passwordService.HashPassword(request.Password),
                IsEmailConfirmed = !_securitySettings.RequireEmailConfirmation,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User created successfully: {Email}", user.Email);

            var userProfile = MapToUserProfile(user);
            return new ApiResponse<UserProfile>(true, "User created successfully", userProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user: {Email}", request.Email);
            return new ApiResponse<UserProfile>(false, "An error occurred while creating the user");
        }
    }

    public async Task<ApiResponse<UserProfile>> UpdateAsync(Guid userId, UpdateProfileRequest request)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return new ApiResponse<UserProfile>(false, "User not found");
            }

            user.FirstName = request.FirstName;
            user.LastName = request.LastName;
            user.DisplayName = request.DisplayName ?? $"{request.FirstName} {request.LastName}";
            user.ProfilePictureUrl = request.ProfilePictureUrl ?? string.Empty;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("User profile updated successfully: {UserId}", userId);

            var userProfile = MapToUserProfile(user);
            return new ApiResponse<UserProfile>(true, "Profile updated successfully", userProfile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user profile: {UserId}", userId);
            return new ApiResponse<UserProfile>(false, "An error occurred while updating the profile");
        }
    }

    public async Task<ApiResponse> DeleteAsync(Guid userId)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return new ApiResponse(false, "User not found");
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User deleted successfully: {UserId}", userId);
            return new ApiResponse(true, "User deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user: {UserId}", userId);
            return new ApiResponse(false, "An error occurred while deleting the user");
        }
    }

    public async Task<ApiResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return new ApiResponse(false, "User not found");
            }

            if (!_passwordService.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            {
                return new ApiResponse(false, "Current password is incorrect");
            }

            var passwordValidation = _passwordService.ValidatePassword(request.NewPassword);
            if (!passwordValidation.IsValid)
            {
                var errors = new Dictionary<string, string[]>
                {
                    ["NewPassword"] = passwordValidation.Errors
                };
                return new ApiResponse(false, "Password validation failed", errors);
            }

            user.PasswordHash = _passwordService.HashPassword(request.NewPassword);
            user.UpdatedAt = DateTime.UtcNow;

            var refreshTokens = await _context.RefreshTokens.Where(rt => rt.UserId == userId && rt.IsActive).ToListAsync();
            foreach (var token in refreshTokens)
            {
                token.Revoked = DateTime.UtcNow;
                token.ReasonRevoked = "Password changed";
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Password changed successfully for user: {UserId}", userId);
            return new ApiResponse(true, "Password changed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for user: {UserId}", userId);
            return new ApiResponse(false, "An error occurred while changing the password");
        }
    }

    public async Task<bool> ExistsAsync(string email)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return await _context.Users.AnyAsync(u => u.Email == normalizedEmail);
    }

    public async Task<bool> IsEmailConfirmedAsync(string email)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        return user?.IsEmailConfirmed ?? false;
    }

    public async Task<ApiResponse> ConfirmEmailAsync(ConfirmEmailRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                return new ApiResponse(false, "User not found");
            }

            if (user.IsEmailConfirmed)
            {
                return new ApiResponse(true, "Email already confirmed");
            }

            var requestTokenHash = _tokenService.HashToken(request.Token);
            if (user.EmailConfirmationToken != requestTokenHash)
            {
                return new ApiResponse(false, "Invalid confirmation token");
            }

            if (user.EmailConfirmationTokenExpires.HasValue && user.EmailConfirmationTokenExpires.Value < DateTime.UtcNow)
            {
                return new ApiResponse(false, "Confirmation token has expired");
            }

            user.IsEmailConfirmed = true;
            user.EmailConfirmationToken = null;
            user.EmailConfirmationTokenExpires = null;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Email confirmed successfully for user: {Email}", user.Email);
            return new ApiResponse(true, "Email confirmed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming email: {Email}", request.Email);
            return new ApiResponse(false, "An error occurred while confirming the email");
        }
    }

    public async Task<ApiResponse> GenerateEmailConfirmationTokenAsync(string email)
    {
        try
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                return new ApiResponse(false, "User not found");
            }

            if (user.IsEmailConfirmed)
            {
                return new ApiResponse(false, "Email already confirmed");
            }

            var rawToken = _tokenService.GenerateEmailConfirmationToken();
            user.EmailConfirmationToken = _tokenService.HashToken(rawToken);
            user.EmailConfirmationTokenExpires = DateTime.UtcNow.AddHours(_securitySettings.EmailConfirmationTokenExpirationHours);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Email confirmation token generated for user: {Email}", normalizedEmail);
            return new ApiResponse(true, "Email confirmation token generated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating email confirmation token: {Email}", email);
            return new ApiResponse(false, "An error occurred while generating the confirmation token");
        }
    }

    public async Task<ApiResponse<string>> GenerateAndSendEmailConfirmationAsync(string email, string baseUrl)
    {
        try
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (user == null)
            {
                return new ApiResponse<string>(false, "User not found");
            }

            if (user.IsEmailConfirmed)
            {
                return new ApiResponse<string>(false, "Email already confirmed");
            }

            var rawToken = _tokenService.GenerateEmailConfirmationToken();
            user.EmailConfirmationToken = _tokenService.HashToken(rawToken);
            user.EmailConfirmationTokenExpires = DateTime.UtcNow.AddHours(_securitySettings.EmailConfirmationTokenExpirationHours);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var emailResult = await _emailService.SendEmailConfirmationAsync(normalizedEmail, rawToken, baseUrl);
            if (!emailResult.Success)
            {
                _logger.LogError("Failed to send confirmation email to {Email}: {Message}", normalizedEmail, emailResult.Message);
                return new ApiResponse<string>(false, "Token generated but failed to send email");
            }

            _logger.LogInformation("Email confirmation token generated and sent for user: {Email}", normalizedEmail);
            return new ApiResponse<string>(true, "Confirmation email sent successfully", string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating and sending confirmation email: {Email}", email);
            return new ApiResponse<string>(false, "An error occurred while processing the confirmation email");
        }
    }

    private static UserProfile MapToUserProfile(User user)
    {
        return new UserProfile(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.DisplayName,
            user.ProfilePictureUrl,
            user.IsEmailConfirmed,
            user.CreatedAt
        );
    }
}
