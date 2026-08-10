using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// Manage teams within an organization. Teams are owned by the organization, not by a
/// project — a project selects one existing team as its assigned team.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class TeamsController : ControllerBase
{
    private readonly ITeamService _teamService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public TeamsController(
        ITeamService teamService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _teamService = teamService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>List all active teams in an organization. Requires Reader on the organization.</summary>
    [HttpGet("api/orgs/{orgId:guid}/teams")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForOrg(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.Reader, ct);
        var result = await _teamService.GetForOrgAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamResponse>>(true, "Teams retrieved.", result));
    }

    /// <summary>Create a new team in an organization. Requires OrgAdmin.</summary>
    [HttpPost("api/orgs/{orgId:guid}/teams")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid orgId, [FromBody] CreateTeamRequest request, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);
        var team = await _teamService.CreateAsync(orgId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { teamId = team.Id },
            new ApiResponse<TeamResponse>(true, "Team created.", team));
    }

    /// <summary>Get a team by ID.</summary>
    [HttpGet("api/teams/{teamId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid teamId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.Reader, ct);
        var team = await _teamService.GetByIdAsync(teamId, ct);
        return Ok(new ApiResponse<TeamResponse>(true, "Team retrieved.", team));
    }

    /// <summary>Update team details. Requires ProjectAdmin.</summary>
    [HttpPut("api/teams/{teamId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(Guid teamId, [FromBody] UpdateTeamRequest request, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        var team = await _teamService.UpdateAsync(teamId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<TeamResponse>(true, "Team updated.", team));
    }

    /// <summary>
    /// Archive a team. Requires ProjectAdmin. Fails with 400 if the team is still assigned
    /// to active projects — reassign those projects first.
    /// </summary>
    [HttpDelete("api/teams/{teamId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid teamId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        await _teamService.DeactivateAsync(teamId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Team archived."));
    }

    /// <summary>Get team members.</summary>
    [HttpGet("api/teams/{teamId:guid}/members")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamMemberResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMembers(Guid teamId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.Reader, ct);
        var result = await _teamService.GetMembersAsync(teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamMemberResponse>>(true, "Members retrieved.", result));
    }

    /// <summary>Add a member to a team. Requires ProjectAdmin.</summary>
    [HttpPost("api/teams/{teamId:guid}/members")]
    [ProducesResponseType(typeof(ApiResponse<TeamMemberResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMember(Guid teamId, [FromBody] AddTeamMemberRequest request, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        var member = await _teamService.AddMemberAsync(teamId, request.UserId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<TeamMemberResponse>(true, "Member added.", member));
    }

    /// <summary>
    /// Check whether a user is a member of the team. Requires Reader.
    /// Returns the membership flag rather than 404-ing on a non-member, so callers can
    /// use it as a plain predicate.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/members/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IsMember(Guid teamId, Guid userId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.Reader, ct);
        var isMember = await _teamService.IsMemberAsync(teamId, userId, ct);
        return Ok(new ApiResponse<bool>(true, isMember ? "User is a team member." : "User is not a team member.", isMember));
    }

    /// <summary>Remove a member from a team. Requires ProjectAdmin.</summary>
    [HttpDelete("api/teams/{teamId:guid}/members/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        await _teamService.RemoveMemberAsync(teamId, userId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Member removed."));
    }

    // -------------------------------------------------------------------------
    private async Task RequireOrgRoleAsync(Guid orgId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Organization, orgId, ct))
            throw new ForbiddenException();
    }

    private async Task RequireTeamRoleAsync(Guid teamId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }
}
