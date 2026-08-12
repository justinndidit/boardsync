using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Repositories;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Controllers;

/// <summary>
/// Public user lookups — the profile fields other people in a workspace are allowed to see.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
[Produces("application/json")]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly ICurrentUserContext _currentUser;

    public UsersController(IUserRepository users, ICurrentUserContext currentUser)
    {
        _users = users;
        _currentUser = currentUser;
    }

    /// <summary>Get a user's public profile by their ID.</summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetProfileByIdAsync(userId, ct)
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

        var user = await _users.GetProfileByEmailAsync(email, ct)
            ?? throw new NotFoundException($"No user found with email '{email}'.");

        return Ok(new ApiResponse<UserProfile>(true, "User found.", user));
    }

    /// <summary>Get the currently authenticated user's own profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var user = await _users.GetProfileByIdAsync(_currentUser.UserId, ct)
            ?? throw new NotFoundException("User", _currentUser.UserId);

        return Ok(new ApiResponse<UserProfile>(true, "Profile retrieved.", user));
    }
}
