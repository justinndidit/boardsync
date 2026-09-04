using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
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
    private readonly IEventBus _eventBus;
    private readonly ICurrentUserContext _currentUser;

    public TeamsController(
        ITeamService teamService,
        IRbacService rbac,
        IEventBus eventBus,
        ICurrentUserContext currentUser)
    {
        _teamService = teamService;
        _rbac = rbac;
        _eventBus = eventBus;
        _currentUser = currentUser;
    }

    /// <summary>List all active teams in an organization. Requires <c>org:read</c>.</summary>
    [HttpGet("api/orgs/{orgId:guid}/teams")]
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForOrg(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _teamService.GetForOrgAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamResponse>>(true, "Teams retrieved.", result));
    }

    /// <summary>List all archived (inactive) teams in an organization. Requires <c>org:read</c>.</summary>
    [HttpGet("api/orgs/{orgId:guid}/teams/archived")]
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetArchived(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _teamService.GetArchivedForOrgAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamResponse>>(true, "Archived teams retrieved.", result));
    }

    /// <summary>Restore an archived team. Requires <c>team:manage</c>.</summary>
    [HttpPost("api/teams/{teamId:guid}/activate")]
    [RequirePermission(Permissions.TeamManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid teamId, CancellationToken ct)
    {
        await _teamService.ActivateAsync(teamId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Team unarchived."));
    }

    /// <summary>Create a new team in an organization. Requires OrgAdmin.</summary>
    [HttpPost("api/orgs/{orgId:guid}/teams")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid orgId, [FromBody] CreateTeamRequest request, CancellationToken ct)
    {
        var team = await _teamService.CreateAsync(orgId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { teamId = team.Id },
            new ApiResponse<TeamResponse>(true, "Team created.", team));
    }

    /// <summary>Get a team by ID.</summary>
    [HttpGet("api/teams/{teamId:guid}")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid teamId, CancellationToken ct)
    {
        var team = await _teamService.GetByIdAsync(teamId, ct);
        return Ok(new ApiResponse<TeamResponse>(true, "Team retrieved.", team));
    }

    /// <summary>Update team details. Requires <c>team:manage</c>.</summary>
    [HttpPut("api/teams/{teamId:guid}")]
    [RequirePermission(Permissions.TeamManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<TeamResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(Guid teamId, [FromBody] UpdateTeamRequest request, CancellationToken ct)
    {
        var team = await _teamService.UpdateAsync(teamId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<TeamResponse>(true, "Team updated.", team));
    }

    /// <summary>
    /// Archive a team. Requires <c>team:manage</c>. Fails with 400 if the team is still assigned
    /// to active projects — reassign those projects first.
    /// </summary>
    [HttpDelete("api/teams/{teamId:guid}")]
    [RequirePermission(Permissions.TeamManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid teamId, CancellationToken ct)
    {
        await _teamService.DeactivateAsync(teamId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Team archived."));
    }

    /// <summary>Get team members.</summary>
    [HttpGet("api/teams/{teamId:guid}/members")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TeamMemberResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMembers(Guid teamId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _teamService.GetMembersAsync(teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<TeamMemberResponse>>(true, "Members retrieved.", result));
    }

    /// <summary>Add a member to a team. Requires <c>team:member:manage</c>.</summary>
    [HttpPost("api/teams/{teamId:guid}/members")]
    [RequirePermission(Permissions.TeamMemberManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<TeamMemberResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMember(Guid teamId, [FromBody] AddTeamMemberRequest request, CancellationToken ct)
    {
        var member = await _teamService.AddMemberAsync(teamId, request.UserId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<TeamMemberResponse>(true, "Member added.", member));
    }

    /// <summary>
    /// Check whether a user is a member of the team. Requires <c>team:read</c>.
    /// Returns the membership flag rather than 404-ing on a non-member, so callers can
    /// use it as a plain predicate.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/members/{userId:guid}")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IsMember(Guid teamId, Guid userId, CancellationToken ct)
    {
        var isMember = await _teamService.IsMemberAsync(teamId, userId, ct);
        return Ok(new ApiResponse<bool>(true, isMember ? "User is a team member." : "User is not a team member.", isMember));
    }

    /// <summary>Remove a member from a team. Requires <c>team:member:manage</c>.</summary>
    [HttpDelete("api/teams/{teamId:guid}/members/{userId:guid}")]
    [RequirePermission(Permissions.TeamMemberManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken ct)
    {
        await _teamService.RemoveMemberAsync(teamId, userId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Member removed."));
    }

    // ── Team roles ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ordinary team-scope roles — everything grantable at team scope that is not a position.
    /// </summary>
    /// <remarks>
    /// Positions are excluded because they are handed over through
    /// <c>PUT /api/teams/{teamId}/positions/{position}</c>, which transfers in one call so the seat
    /// is never half empty. Granting one through here would let two people hold it at once.
    /// </remarks>
    private static readonly IReadOnlyList<RoleType> AssignableTeamRoles =
        [.. RolePermissions.GrantableToUsersAt(RoleScope.Team)
            .Where(role => !TeamPositions.Includes(role))];

    /// <summary>The team's ordinary role assignments. Requires <c>team:read</c>.</summary>
    /// <remarks>
    /// Positions are not listed here — <c>GET .../positions</c> answers for those, and reporting a
    /// Scrum Master as an ordinary grant would invite somebody to revoke it through the wrong door.
    /// </remarks>
    [HttpGet("api/teams/{teamId:guid}/roles")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TeamRoleResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRoles(Guid teamId, CancellationToken ct)
    {
        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);

        var items = assignments
            .Where(r => !TeamPositions.Includes(r.Role))
            .Select(r => new TeamRoleResponse(r.UserId, r.Role, r.CreatedAt))
            .OrderBy(r => r.Role)
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<TeamRoleResponse>>(true, "Team roles retrieved.", items));
    }

    /// <summary>
    /// Grant a team-scope role, replacing any ordinary team role the user already holds.
    /// Requires <c>team:role:assign</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Tester</c> is the reason this endpoint exists.</b> There was no way to grant an
    /// ordinary team-scope role at all: membership carries none, and positions cover only the three
    /// appointments. So a team Tester — who certifies work on <em>every</em> project the team serves,
    /// through the team → project edge — could not be appointed, and the QA gate could only be
    /// passed by people holding certification for some other reason.
    /// </para>
    /// <para>
    /// The grantee must already be a member of the team. A role is what somebody's membership means,
    /// not a way to join.
    /// </para>
    /// </remarks>
    [HttpPost("api/teams/{teamId:guid}/roles")]
    [RequirePermission(Permissions.TeamRoleAssign, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<TeamRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRole(
        Guid teamId,
        [FromBody] AssignTeamRoleRequest request,
        CancellationToken ct)
    {
        if (!AssignableTeamRoles.Contains(request.Role))
            return BadRequest(new ApiResponse(false,
                $"'{request.Role}' cannot be assigned at team scope. Valid roles: " +
                $"{string.Join(", ", AssignableTeamRoles)}. " +
                "Team Lead, Scrum Master and Product Owner are positions — use the positions endpoint."));

        // 404s if the team does not exist.
        _ = await _teamService.GetByIdAsync(teamId, ct);

        if (!await _teamService.IsMemberAsync(teamId, request.UserId, ct))
            return BadRequest(new ApiResponse(false,
                "User must be a member of the team before receiving a team role."));

        // One ordinary team role per user. Positions are left alone: they are a separate seat, and
        // a Scrum Master who is also the team's Tester is a real and reasonable arrangement.
        var existing = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);

        foreach (var held in existing.Where(r =>
                     r.UserId == request.UserId && !TeamPositions.Includes(r.Role)))
        {
            await _rbac.RemoveRoleAsync(request.UserId, held.Role, RoleScope.Team, teamId, ct);
        }

        var assignment = await _rbac.AssignRoleAsync(
            request.UserId, request.Role, RoleScope.Team, teamId, _currentUser.UserId, ct: ct);

        return Ok(new ApiResponse<TeamRoleResponse>(true, $"Role updated to {request.Role}.",
            new TeamRoleResponse(assignment.UserId, assignment.Role, assignment.CreatedAt)));
    }

    /// <summary>
    /// Revoke a user's ordinary team-scope role. Requires <c>team:role:assign</c>.
    /// </summary>
    /// <remarks>
    /// Positions are untouched — vacating one goes through
    /// <c>DELETE .../positions/{position}</c>, which is a different act with a different guard.
    /// </remarks>
    [HttpDelete("api/teams/{teamId:guid}/roles/{userId:guid}")]
    [RequirePermission(Permissions.TeamRoleAssign, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRole(Guid teamId, Guid userId, CancellationToken ct)
    {
        var existing = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);

        var held = existing
            .Where(r => r.UserId == userId && !TeamPositions.Includes(r.Role))
            .ToList();

        if (held.Count == 0)
            throw new NotFoundException($"User {userId} holds no team role here.");

        foreach (var role in held)
            await _rbac.RemoveRoleAsync(userId, role.Role, RoleScope.Team, teamId, ct);

        return Ok(new ApiResponse(true, "Team role revoked."));
    }

    // ── Team positions ────────────────────────────────────────────────────────

    /// <summary>
    /// List the team's positions and who holds each. Requires read access to the team.
    /// </summary>
    /// <remarks>
    /// Every position is listed whether or not it is filled, so a vacancy is visible rather than
    /// being an absence the caller has to infer.
    /// </remarks>
    [HttpGet("api/teams/{teamId:guid}/positions")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TeamPositionResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPositions(Guid teamId, CancellationToken ct)
    {

        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);

        var positions = TeamPositions.All
            .Select(position => new TeamPositionResponse(
                position,
                assignments.FirstOrDefault(a => a.Role == position)?.UserId))
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<TeamPositionResponse>>(
            true, "Positions retrieved.", positions));
    }

    /// <summary>
    /// Appoint someone to a team position, taking it from whoever holds it.
    /// Requires <c>team:role:assign</c> — Team Lead or an org admin.
    /// </summary>
    /// <remarks>
    /// One call rather than revoke-then-assign, so there is never a moment with the position half
    /// transferred. The current holder may also hand over their own position; an org admin is the
    /// backstop for when they cannot, which is the case this exists for.
    /// </remarks>
    [HttpPut("api/teams/{teamId:guid}/positions/{position}")]
    [PermissionCheckedInAction(
        "team:role:assign, or being the current holder handing over their own position.")]
    [ProducesResponseType(typeof(ApiResponse<TeamPositionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignPosition(
        Guid teamId,
        RoleType position,
        [FromBody] AssignTeamPositionRequest request,
        CancellationToken ct)
    {
        if (!TeamPositions.Includes(position))
            return BadRequest(new ApiResponse(false,
                $"'{position}' is not a team position. Valid positions: {string.Join(", ", TeamPositions.All)}."));

        await RequirePositionAuthorityAsync(teamId, position, ct);

        // A position is authority over a team's work, so it belongs to someone doing that work.
        // Mirrors the rule that a project role requires organization membership first.
        if (!await _teamService.IsMemberAsync(teamId, request.UserId, ct))
            return BadRequest(new ApiResponse(false,
                "User must be a member of the team before holding one of its positions."));

        var team = await _teamService.GetByIdAsync(teamId, ct);

        // Staged before the transfer, not after: TransferTeamPositionAsync saves the shared unit of
        // work, so enqueuing first is what makes the outbox row and the role change commit together.
        var holders = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);
        var current = holders.FirstOrDefault(a => a.Role == position && a.UserId != request.UserId)?.UserId;

        _eventBus.Enqueue(new TeamPositionTransferred(
            teamId, team.OrganizationId, position, current, request.UserId, _currentUser.UserId));

        var previous = await _rbac.TransferTeamPositionAsync(
            teamId, position, request.UserId, _currentUser.UserId, ct);

        return Ok(new ApiResponse<TeamPositionResponse>(
            true,
            previous is null ? $"{position} appointed." : $"{position} transferred.",
            new TeamPositionResponse(position, request.UserId)));
    }

    /// <summary>
    /// Leave a team position vacant. Requires <c>team:role:assign</c>.
    /// </summary>
    [HttpDelete("api/teams/{teamId:guid}/positions/{position}")]
    [PermissionCheckedInAction(
        "team:role:assign, or being the current holder.")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> VacatePosition(Guid teamId, RoleType position, CancellationToken ct)
    {
        if (!TeamPositions.Includes(position))
            return BadRequest(new ApiResponse(false, $"'{position}' is not a team position."));

        await RequirePositionAuthorityAsync(teamId, position, ct);

        var team = await _teamService.GetByIdAsync(teamId, ct);

        // Read the holder and stage the event before the removal, for the same reason as the
        // transfer above — the vacate call is what saves.
        var holders = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);
        var held = holders.FirstOrDefault(a => a.Role == position)?.UserId;

        if (held is null)
            return Ok(new ApiResponse(true, "Position was already vacant."));

        _eventBus.Enqueue(new TeamPositionVacated(
            teamId, team.OrganizationId, position, held.Value, _currentUser.UserId));

        await _rbac.VacateTeamPositionAsync(teamId, position, ct);

        return Ok(new ApiResponse(true, "Position vacated."));
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Permits the change when the caller may assign positions, or when they are handing over the
    /// one they hold themselves.
    /// </summary>
    private async Task RequirePositionAuthorityAsync(Guid teamId, RoleType position, CancellationToken ct)
    {
        if (await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.TeamRoleAssign, RoleScope.Team, teamId, ct))
            return;

        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Team, teamId, ct);

        var isHolder = assignments.Any(a => a.Role == position && a.UserId == _currentUser.UserId);

        if (!isHolder) throw new ForbiddenException();
    }

    private async Task RequireOrgAsync(Guid orgId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Organization, orgId, ct))
            throw new ForbiddenException();
    }

    private async Task RequireTeamAsync(Guid teamId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }
}
