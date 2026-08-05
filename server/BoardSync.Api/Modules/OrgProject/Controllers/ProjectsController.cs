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
/// Manage projects within an organization.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class ProjectsController : ControllerBase
{
    /// <summary>
    /// Roles that are meaningful at project scope. OrgAdmin is organization-wide (and already
    /// cascades down to every project in its org), and User is the "no permissions yet" default
    /// carried by every authenticated account — neither is assignable here.
    /// </summary>
    private static readonly RoleType[] AssignableProjectRoles =
        [RoleType.ProjectAdmin, RoleType.TeamMember, RoleType.Reader];

    private readonly IProjectService _projectService;
    private readonly IOrganizationService _orgService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public ProjectsController(
        IProjectService projectService,
        IOrganizationService orgService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _projectService = projectService;
        _orgService = orgService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>List all projects in an organization.</summary>
    [HttpGet("api/orgs/{orgId:guid}/projects")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProjectSummaryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForOrg(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.Reader, ct);
        var result = await _projectService.GetForOrgAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<ProjectSummaryResponse>>(true, "Projects retrieved.", result));
    }

    /// <summary>Create a new project within an organization. Requires OrgAdmin.</summary>
    [HttpPost("api/orgs/{orgId:guid}/projects")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid orgId, [FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);
        var project = await _projectService.CreateAsync(orgId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { projectId = project.Id },
            new ApiResponse<ProjectResponse>(true, "Project created.", project));
    }

    /// <summary>Get a project by ID.</summary>
    [HttpGet("api/projects/{projectId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid projectId, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.Reader, ct);
        var project = await _projectService.GetByIdAsync(projectId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project retrieved.", project));
    }

    /// <summary>Update project details. Requires ProjectAdmin.</summary>
    [HttpPut("api/projects/{projectId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid projectId, [FromBody] UpdateProjectRequest request, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.ProjectAdmin, ct);
        var project = await _projectService.UpdateAsync(projectId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project updated.", project));
    }

    /// <summary>
    /// Reassign the project to a different team in the same organization. Requires ProjectAdmin.
    /// The project's board follows the new team, so its cards come from that team's active sprint.
    /// </summary>
    [HttpPut("api/projects/{projectId:guid}/team")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignTeam(
        Guid projectId,
        [FromBody] AssignProjectTeamRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.ProjectAdmin, ct);
        var project = await _projectService.AssignTeamAsync(
            projectId, request.AssignedTeamId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project team reassigned.", project));
    }

    // ── Project-scope roles ───────────────────────────────────────────────────

    /// <summary>
    /// List the project-scope role assignments for a project. Requires Reader.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/roles")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectRoleResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRoles(Guid projectId, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.Reader, ct);

        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Project, projectId, ct);
        var items = assignments
            .Select(r => new ProjectRoleResponse(r.UserId, r.Role, r.CreatedAt))
            .OrderBy(r => r.Role)
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<ProjectRoleResponse>>(true, "Project roles retrieved.", items));
    }

    /// <summary>
    /// Grant a user a role on this project, replacing any project-scope role they already hold.
    /// Requires ProjectAdmin. Valid roles: ProjectAdmin, TeamMember, Reader.
    /// </summary>
    /// <remarks>
    /// This is how a member of the organization gains access to a project's work items —
    /// organization membership alone deliberately grants nothing inside a project.
    /// </remarks>
    [HttpPost("api/projects/{projectId:guid}/roles")]
    [ProducesResponseType(typeof(ApiResponse<ProjectRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRole(
        Guid projectId,
        [FromBody] AssignProjectRoleRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.ProjectAdmin, ct);

        if (!AssignableProjectRoles.Contains(request.Role))
            return BadRequest(new ApiResponse(false,
                $"'{request.Role}' cannot be assigned at project scope. Valid roles: {string.Join(", ", AssignableProjectRoles)}."));

        // 404s if the project does not exist, and gives us the owning organization.
        var project = await _projectService.GetByIdAsync(projectId, ct);

        // The grantee must already belong to the owning organization: a project role must never be
        // a back door into an org the user was never added to.
        if (!await _orgService.IsMemberAsync(project.OrganizationId, request.UserId, ct))
            return BadRequest(new ApiResponse(false,
                "User must be a member of the organization before receiving a project role."));

        // One project-scope role per user — drop whatever they held first.
        var existing = await _rbac.GetScopeRolesAsync(RoleScope.Project, projectId, ct);
        foreach (var held in existing.Where(r => r.UserId == request.UserId))
            await _rbac.RemoveRoleAsync(request.UserId, held.Role, RoleScope.Project, projectId, ct);

        var assignment = await _rbac.AssignRoleAsync(
            request.UserId, request.Role, RoleScope.Project, projectId, _currentUser.UserId, ct);

        return Ok(new ApiResponse<ProjectRoleResponse>(true, $"Role updated to {request.Role}.",
            new ProjectRoleResponse(assignment.UserId, assignment.Role, assignment.CreatedAt)));
    }

    /// <summary>
    /// Revoke a user's project-scope role. Requires ProjectAdmin.
    /// The last remaining ProjectAdmin cannot be revoked.
    /// </summary>
    [HttpDelete("api/projects/{projectId:guid}/roles/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRole(Guid projectId, Guid userId, CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.ProjectAdmin, ct);

        var existing = await _rbac.GetScopeRolesAsync(RoleScope.Project, projectId, ct);
        var held = existing.Where(r => r.UserId == userId).ToList();

        if (held.Count == 0)
            throw new NotFoundException($"User {userId} holds no role in this project.");

        // Refuse to strip the last ProjectAdmin — nobody could administer the project afterwards.
        if (held.Any(r => r.Role == RoleType.ProjectAdmin) &&
            existing.Count(r => r.Role == RoleType.ProjectAdmin) == 1)
            return BadRequest(new ApiResponse(false, "Cannot revoke the last ProjectAdmin of a project."));

        foreach (var role in held)
            await _rbac.RemoveRoleAsync(userId, role.Role, RoleScope.Project, projectId, ct);

        return Ok(new ApiResponse(true, "Project role revoked."));
    }

    // -------------------------------------------------------------------------
    private async Task RequireOrgRoleAsync(Guid orgId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Organization, orgId, ct))
            throw new ForbiddenException();
    }

    private async Task RequireProjectRoleAsync(Guid projectId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();
    }
}

/// <summary>Request body for granting a user a project-scope role.</summary>
public record AssignProjectRoleRequest(
    [System.ComponentModel.DataAnnotations.Required] Guid UserId,
    [System.ComponentModel.DataAnnotations.Required] RoleType Role
);

/// <summary>A project-scope role assignment.</summary>
public record ProjectRoleResponse(
    Guid UserId,
    RoleType Role,
    DateTime AssignedAt
);
