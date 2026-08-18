using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Repositories;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BoardSync.Api.Modules.Rbac.Models;

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
    [NoPermissionRequired(
        "Any authenticated user may resolve a user id to a public profile. See docs/permissions-model.md 3.11 — this is a directory-visibility decision, not an oversight.")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetProfileByIdAsync(userId, ct)
            ?? throw new NotFoundException("User", userId);

        return Ok(new ApiResponse<UserProfile>(true, "User found.", user));
    }

    /// <summary>
    /// Look a user up by email address (exact match). Requires the caller to manage members of some
    /// organization.
    /// </summary>
    /// <remarks>
    /// This is the invite flow's lookup, and it is restricted to people who can actually invite.
    /// Left open to any authenticated user it was a cross-tenant directory: anyone with an account
    /// could confirm whether a given address belonged to a user here and read their name.
    /// <para>
    /// Checked with <c>anywhere</c> rather than against an organization because there is no
    /// organization to check against — the person being looked up is, by definition, not yet in
    /// yours. This establishes that the caller administers members somewhere; it cannot establish
    /// more than that, and the residual exposure is recorded in
    /// <c>docs/permissions-model.md</c> §9.6.
    /// </para>
    /// </remarks>
    [HttpGet("by-email")]
    [RequirePermissionAnywhere(Permissions.OrgMemberManage)]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
    [NoPermissionRequired(
        "Returns the caller\u0027s own profile.")]
    [ProducesResponseType(typeof(ApiResponse<UserProfile>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var user = await _users.GetProfileByIdAsync(_currentUser.UserId, ct)
            ?? throw new NotFoundException("User", _currentUser.UserId);

        return Ok(new ApiResponse<UserProfile>(true, "Profile retrieved.", user));
    }
}
