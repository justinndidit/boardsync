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

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthenticationService _authService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthenticationService authService,
        IUserService userService,
        IEmailService emailService,
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _userService = userService;
        _emailService = emailService;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
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

    [HttpPost("register")]
    [AllowAnonymous]
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
                var welcomeResult = await _emailService.SendWelcomeEmailAsync(result.Data.Email, result.Data.FirstName);
                if (!welcomeResult.Success)
                {
                    _logger.LogWarning("Failed to send welcome email to {Email}: {Message}",
                        result.Data.Email, welcomeResult.Message);
                }
            }
            // Send confirmation email if required
            else
            {
                var baseUrl = Request.Headers.Origin.FirstOrDefault() ?? $"{Request.Scheme}://{Request.Host}";
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

    [HttpPost("logout")]
    [Authorize]
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

    [HttpPost("refresh-token")]
    [AllowAnonymous]
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

    [HttpPost("revoke-token")]
    [Authorize]
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

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var result = await _authService.ForgotPasswordAsync(request);
        return Ok(result);
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var result = await _authService.ResetPasswordAsync(request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
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
            await _emailService.SendWelcomeEmailAsync(userResult.Data.Email, userResult.Data.FirstName);
        }

        return Ok(result);
    }

    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest request)
    {
        var baseUrl = Request.Headers.Origin.FirstOrDefault() ?? $"{Request.Scheme}://{Request.Host}";
        var result = await _userService.GenerateAndSendEmailConfirmationAsync(request.Email, baseUrl);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(new ApiResponse(true, "Confirmation email sent successfully"));
    }

    [HttpPost("change-password")]
    [Authorize]
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

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetCurrentUserId();
        var result = await _userService.GetByIdAsync(userId);

        if (!result.Success)
        {
            return NotFound(result);
        }

        return Ok(result);
    }

    [HttpPut("profile")]
    [Authorize]
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

public record RevokeTokenRequest(string? Token = null);
public record ResendConfirmationRequest([System.ComponentModel.DataAnnotations.Required] string Email);