using BoardSync.Api.Data;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Controllers;

/// <summary>
/// User lookup endpoints — used primarily when inviting members to orgs/teams.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
[Produces("application/json")]
public class UsersController : ControllerBase
{
    private readonly BoardSyncDbContext _context;
    private readonly ICurrentUserContext _currentUser;

    public UsersController(BoardSyncDbContext context, ICurrentUserContext currentUser)
    {
        _context = context;
        _currentUser = currentUser;
    }

    /// <summary>Get a user's public profile by their ID.</summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid userId, CancellationToken ct)
    {
        var user = await _context.Users
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new UserProfile(
                u.Id, u.Email, u.FirstName, u.LastName,
                u.DisplayName, u.ProfilePictureUrl,
                u.IsEmailConfirmed, u.CreatedAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("User", userId);

        return Ok(new ApiResponse<UserProfile>(true, "User found.", user));
    }

    /// <summary>
    /// Search users by email address (exact match).
    /// Used by OrgAdmins when inviting users to an organization.
    /// </summary>
    [HttpGet("by-email")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByEmail([FromQuery] string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new ApiResponse(false, "Email is required."));

        var normalized = email.Trim().ToLowerInvariant();

        var user = await _context.Users
            .Where(u => u.Email == normalized && u.IsActive)
            .Select(u => new UserProfile(
                u.Id, u.Email, u.FirstName, u.LastName,
                u.DisplayName, u.ProfilePictureUrl,
                u.IsEmailConfirmed, u.CreatedAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"No user found with email '{email}'.");

        return Ok(new ApiResponse<UserProfile>(true, "User found.", user));
    }

    /// <summary>Get the currently authenticated user's own profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var user = await _context.Users
            .Where(u => u.Id == _currentUser.UserId && u.IsActive)
            .Select(u => new UserProfile(
                u.Id, u.Email, u.FirstName, u.LastName,
                u.DisplayName, u.ProfilePictureUrl,
                u.IsEmailConfirmed, u.CreatedAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("User", _currentUser.UserId);

        return Ok(new ApiResponse<UserProfile>(true, "Profile retrieved.", user));
    }
}
