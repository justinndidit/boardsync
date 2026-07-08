using BoardSync.Api.Modules.OrgProject.DTOs;
using BoardSync.Api.Modules.OrgProject.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// Manage teams within a project.
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

    /// <summary>List all teams in a project.</summary>
    [HttpGet("api/projects/{projectId:guid}/teams")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForProject(Guid projectId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.Reader, ct);
        var result = await _teamService.GetForProjectAsync(projectId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamResponse>>(true, "Teams retrieved.", result));
    }

    /// <summary>Create a new team in a project. Requires ProjectAdmin.</summary>
    [HttpPost("api/projects/{projectId:guid}/teams")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(Guid projectId, [FromBody] CreateTeamRequest request, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.ProjectAdmin, ct);
        var team = await _teamService.CreateAsync(projectId, request, _currentUser.UserId, ct);
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

    /// <summary>Remove a member from a team. Requires ProjectAdmin.</summary>
    [HttpDelete("api/teams/{teamId:guid}/members/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        await _teamService.RemoveMemberAsync(teamId, userId, ct);
        return Ok(new ApiResponse(true, "Member removed."));
    }

    // -------------------------------------------------------------------------
    private async Task RequireProjectRoleAsync(Guid projectId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();
    }

    private async Task RequireTeamRoleAsync(Guid teamId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }
}
