using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth.Attributes;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace BoardSync.Api.Controllers;

/// <summary>
/// Authentication and user management endpoints
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IAuthenticationService _authService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IRbacService _rbac;
    private readonly JwtSettings _jwtSettings;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthenticationService authService,
        IUserService userService,
        IEmailService emailService,
        IRbacService rbac,
        IOptions<JwtSettings> jwtSettings,
        IOptions<EmailSettings> emailSettings,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _userService = userService;
        _emailService = emailService;
        _rbac = rbac;
        _jwtSettings = jwtSettings.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Authenticate user and return JWT tokens
    /// </summary>
    /// <param name="request">User login credentials</param>
    /// <returns>Authentication response with access token and user profile</returns>
    /// <response code="200">User authenticated successfully</response>
    /// <response code="400">Invalid credentials or validation errors</response>
    /// <response code="429">Too many authentication attempts</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var ipAddress = GetIpAddress();
        var result = await _authService.LoginAsync(request, ipAddress);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        if (!string.IsNullOrWhiteSpace(result.Data.refreshToken))
        {
            SetRefreshTokenCookie(result.Data.refreshToken);

            return Ok(new ApiResponse<AuthResponse>(
                true,
                result.Message,
                result.Data.authResponse,
                result.Errors));
        }

        return BadRequest(new ApiResponse(false, "Login failed"));
    }

    /// <summary>
    /// Register a new user account
    /// </summary>
    /// <param name="request">User registration information</param>
    /// <returns>User creation result and optional email confirmation requirement</returns>
    /// <response code="200">User registered successfully</response>
    /// <response code="400">Registration failed due to validation errors</response>
    /// <response code="500">Failed to send confirmation email</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _userService.CreateAsync(request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        if (result.Data != null)
        {
            // Send welcome email if email confirmation is not required
            if (result.Data.IsEmailConfirmed)
            {
                var baseUrl = GetAppBaseUrl();
                var welcomeResult = await _emailService.SendWelcomeEmailAsync(result.Data.Email, result.Data.FirstName, baseUrl);
                if (!welcomeResult.Success)
                {
                    _logger.LogWarning("Failed to send welcome email to {Email}: {Message}",
                        result.Data.Email, welcomeResult.Message);
                }
            }
            // Send confirmation email if required
            else
            {
                var baseUrl = GetAppBaseUrl();
                var emailResult = await _userService.GenerateAndSendEmailConfirmationAsync(result.Data.Email, baseUrl);

                if (!emailResult.Success)
                {
                    _logger.LogError("Failed to send confirmation email to {Email}: {Message}",
                        result.Data.Email, emailResult.Message);
                    return StatusCode(500, new ApiResponse(false, "User created but failed to send confirmation email. Please try resend confirmation."));
                }
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Log out the current user and invalidate refresh token
    /// </summary>
    /// <returns>Logout confirmation</returns>
    /// <response code="200">User logged out successfully</response>
    /// <response code="401">User not authenticated</response>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        var userId = GetCurrentUserId();
        var refreshToken = Request.Cookies["refreshToken"];

        var result = await _authService.LogoutAsync(userId, refreshToken);

        if (result.Success)
        {
            Response.Cookies.Delete("refreshToken");
        }

        return Ok(result);
    }

    /// <summary>
    /// Refresh JWT access token using refresh token
    /// </summary>
    /// <param name="request">Optional refresh token in request body (will use cookie if not provided)</param>
    /// <returns>New access token</returns>
    /// <response code="200">Token refreshed successfully</response>
    /// <response code="400">Invalid or expired refresh token</response>
    [HttpPost("refresh-token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<TokenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest? request = null)
    {
        var token = request?.RefreshToken ?? Request.Cookies["refreshToken"];

        if (string.IsNullOrEmpty(token))
        {
            return BadRequest(new ApiResponse(false, "Refresh token is required"));
        }

        var refreshRequest = new RefreshTokenRequest(token);
        var ipAddress = GetIpAddress();

        var result = await _authService.RefreshTokenAsync(refreshRequest, ipAddress);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        if (!string.IsNullOrWhiteSpace(result.Data.refreshToken))
        {
            SetRefreshTokenCookie(result.Data.refreshToken);

            return Ok(new ApiResponse<TokenResponse>(
                true,
                result.Message,
                result.Data.tokenResponse,
                result.Errors));
        }

        return BadRequest(new ApiResponse(false, "Failed to refresh token"));
    }

    /// <summary>
    /// Revoke a refresh token to prevent future use
    /// </summary>
    /// <param name="request">Optional token to revoke (will use cookie if not provided)</param>
    /// <returns>Token revocation confirmation</returns>
    /// <response code="200">Token revoked successfully</response>
    /// <response code="400">Token not provided</response>
    /// <response code="401">User not authenticated</response>
    [HttpPost("revoke-token")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeToken([FromBody] RevokeTokenRequest? request = null)
    {
        var token = request?.Token ?? Request.Cookies["refreshToken"];

        if (string.IsNullOrEmpty(token))
        {
            return BadRequest(new ApiResponse(false, "Token is required"));
        }

        var userId = GetCurrentUserId();
        var ipAddress = GetIpAddress();

        var result = await _authService.RevokeTokenAsync(token, ipAddress, userId);
        return Ok(result);
    }

    /// <summary>
    /// Request password reset email for forgot password
    /// </summary>
    /// <param name="request">Email address for password reset</param>
    /// <returns>Password reset email confirmation</returns>
    /// <response code="200">Password reset email sent (or user not found message for security)</response>
    /// <response code="429">Too many password reset attempts</response>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var result = await _authService.ForgotPasswordAsync(request);
        return Ok(result);
    }

    /// <summary>
    /// Reset user password using reset token
    /// </summary>
    /// <param name="email">Email address for the password reset request</param>
    /// <param name="token">Password reset token sent to the user</param>
    /// <returns>Password reset confirmation</returns>
    /// <response code="200">Password reset successfully</response>
    /// <response code="400">Invalid token or validation errors</response>
    /// <response code="429">Too many password reset attempts</response>
    [HttpGet("reset-password")]
    [AllowAnonymous]
    public IActionResult ResetPasswordGet([FromQuery] string email, [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return Redirect("http://localhost:5173/auth/login?status=invalid");

        var frontendUrl = $"http://localhost:5173/auth/login?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        return Redirect(frontendUrl);
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var result = await _authService.ResetPasswordAsync(request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Confirm user email address using confirmation token
    /// </summary>
    /// <param name="email">Email address to confirm</param>
    /// <param name="token">Confirmation token sent to the user</param>
    /// <returns>Email confirmation result</returns>
    /// <response code="200">Email confirmed successfully</response>
    /// <response code="400">Invalid token or email already confirmed</response>
    [HttpGet("confirm-email")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmEmailGet([FromQuery] string email, [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return Redirect("http://localhost:5173/auth/login?status=invalid");

        var result = await _userService.ConfirmEmailAsync(new ConfirmEmailRequest(email, token));

        if (!result.Success)
            return Redirect("http://localhost:5173/auth/login?status=invalid");

        var frontendUrl = $"http://localhost:5173/auth/login?email={Uri.EscapeDataString(email)}&status=confirmed";
        return Redirect(frontendUrl);
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request)
    {
        var result = await _userService.ConfirmEmailAsync(request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        // Send welcome email after confirmation
        var userResult = await _userService.GetByEmailAsync(request.Email);
        if (userResult.Success && userResult.Data != null)
        {
            var baseUrl = GetAppBaseUrl();
            await _emailService.SendWelcomeEmailAsync(userResult.Data.Email, userResult.Data.FirstName, baseUrl);
        }

        return Ok(result);
    }

    /// <summary>
    /// Resend email confirmation for unconfirmed accounts
    /// </summary>
    /// <param name="request">Email address to resend confirmation</param>
    /// <returns>Resend confirmation result</returns>
    /// <response code="200">Confirmation email sent successfully</response>
    /// <response code="400">Email not found or already confirmed</response>
    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest request)
    {
        var baseUrl = GetAppBaseUrl();
        var result = await _userService.GenerateAndSendEmailConfirmationAsync(request.Email, baseUrl);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(new ApiResponse(true, "Confirmation email sent successfully"));
    }

    /// <summary>
    /// Change current user's password
    /// </summary>
    /// <param name="request">Current and new password information</param>
    /// <returns>Password change confirmation</returns>
    /// <response code="200">Password changed successfully</response>
    /// <response code="400">Invalid current password or validation errors</response>
    /// <response code="401">User not authenticated</response>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.ChangePasswordAsync(userId, request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns the currently authenticated user profile based on the server-validated bearer token.
    /// </summary>
    /// <returns>Authenticated user profile data with roles</returns>
    /// <response code="200">Token is valid and user profile was returned</response>
    /// <response code="401">Token is missing or invalid</response>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.GetByIdAsync(userId);

        if (!result.Success || result.Data is null)
            return NotFound(result);

        var assignments = await _rbac.GetUserRolesAsync(userId, ct);
        var roles = assignments
            .Select(ra => new UserRoleResponse(ra.Role, ra.Scope, ra.ScopeId))
            .ToList();

        var profile = result.Data with { Roles = roles };
        return Ok(new ApiResponse<UserProfile>(true, "Token is valid.", profile));
    }

    /// <summary>
    /// Get current user's profile information including all role assignments.
    /// </summary>
    /// <returns>User profile data with roles</returns>
    /// <response code="200">Profile retrieved successfully</response>
    /// <response code="401">User not authenticated</response>
    /// <response code="404">User profile not found</response>
    [HttpGet("profile")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.GetByIdAsync(userId);

        if (!result.Success || result.Data is null)
            return NotFound(result);

        var assignments = await _rbac.GetUserRolesAsync(userId, ct);
        var roles = assignments
            .Select(ra => new UserRoleResponse(ra.Role, ra.Scope, ra.ScopeId))
            .ToList();

        var profile = result.Data with { Roles = roles };
        return Ok(new ApiResponse<UserProfile>(true, "Profile retrieved successfully.", profile));
    }

    /// <summary>
    /// Update current user's profile information
    /// </summary>
    /// <param name="request">Updated profile information</param>
    /// <returns>Profile update confirmation</returns>
    /// <response code="200">Profile updated successfully</response>
    /// <response code="400">Validation errors</response>
    /// <response code="401">User not authenticated</response>
    [HttpPut("profile")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.UpdateAsync(userId, request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    private void SetRefreshTokenCookie(string token)
    {
        var isDevelopment = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = !isDevelopment || HttpContext.Request.IsHttps
        };

        Response.Cookies.Append("refreshToken", token, cookieOptions);
    }

    private string GetIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private string GetAppBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_emailSettings.BaseUrl))
        {
            return _emailSettings.BaseUrl;
        }

        return $"{Request.Scheme}://{Request.Host}";
    }

    private Guid GetCurrentUserId()
    {
        var userIdClaim = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return userId;
        }
        throw new UnauthorizedAccessException("User ID not found in token");
    }
}

/// <summary>
/// Request to revoke a specific refresh token
/// </summary>
/// <param name="Token">Refresh token to revoke (optional, will use cookie if not provided)</param>
public record RevokeTokenRequest(string? Token = null);

/// <summary>
/// Request to resend email confirmation
/// </summary>
/// <param name="Email">Email address to resend confirmation to</param>
public record ResendConfirmationRequest([System.ComponentModel.DataAnnotations.Required] string Email);