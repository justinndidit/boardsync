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
/// Manage projects within an organization.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class ProjectsController : ControllerBase
{
    /// <summary>
    /// Roles that are meaningful at project scope — <c>ProjectAdmin</c>, <c>Contributor</c> and
    /// <c>Viewer</c>.
    /// </summary>
    /// <remarks>
    /// Read from the permission table rather than written out here, so it cannot disagree with what
    /// the table and the check constraint accept. OrgAdmin is absent because it is organization-wide
    /// and already reaches every project in its organization; granting it on one project would be a
    /// narrower thing wearing a wider name.
    /// </remarks>
    private static readonly IReadOnlyList<RoleType> AssignableProjectRoles =
        RolePermissions.GrantableToUsersAt(RoleScope.Project);

    private readonly IProjectService _projectService;
    private readonly IOrganizationService _orgService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;
    private readonly IEventBus _eventBus;

    public ProjectsController(
        IProjectService projectService,
        IOrganizationService orgService,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        IEventBus eventBus)
    {
        _projectService = projectService;
        _orgService = orgService;
        _rbac = rbac;
        _currentUser = currentUser;
        _eventBus = eventBus;
    }

    /// <summary>List all projects in an organization.</summary>
    [HttpGet("api/orgs/{orgId:guid}/projects")]
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProjectResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForOrg(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _projectService.GetForOrgAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<ProjectResponse>>(true, "Projects retrieved.", result));
    }

    /// <summary>Create a new project within an organization. Requires OrgAdmin.</summary>
    [HttpPost("api/orgs/{orgId:guid}/projects")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid orgId, [FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        var project = await _projectService.CreateAsync(orgId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { projectId = project.Id },
            new ApiResponse<ProjectResponse>(true, "Project created.", project));
    }

    /// <summary>Get a project by ID.</summary>
    [HttpGet("api/projects/{projectId:guid}")]
    [RequirePermission(Permissions.ProjectRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid projectId, CancellationToken ct)
    {
        var project = await _projectService.GetByIdAsync(projectId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project retrieved.", project));
    }

    /// <summary>Update project details. Requires <c>project:admin</c>.</summary>
    [HttpPut("api/projects/{projectId:guid}")]
    [RequirePermission(Permissions.ProjectAdmin, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid projectId, [FromBody] UpdateProjectRequest request, CancellationToken ct)
    {
        var project = await _projectService.UpdateAsync(projectId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project updated.", project));
    }

    /// <summary>
    /// Reassign the project to a different team in the same organization. Requires <c>project:admin</c>.
    /// The project's board follows the new team, so its cards come from that team's active sprint.
    /// </summary>
    [HttpPut("api/projects/{projectId:guid}/team")]
    [RequirePermission(Permissions.ProjectAdmin, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignTeam(
        Guid projectId,
        [FromBody] AssignProjectTeamRequest request,
        CancellationToken ct)
    {
        var project = await _projectService.AssignTeamAsync(
            projectId, request.AssignedTeamId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<ProjectResponse>(true, "Project team reassigned.", project));
    }

    // ── Project-scope roles ───────────────────────────────────────────────────

    /// <summary>
    /// List the project-scope role assignments for a project. Requires <c>project:read</c>.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/roles")]
    [RequirePermission(Permissions.ProjectRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectRoleResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRoles(Guid projectId, CancellationToken ct)
    {

        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Project, projectId, ct);
        var items = assignments
            .Select(r => new ProjectRoleResponse(r.UserId, r.Role, r.CreatedAt))
            .OrderBy(r => r.Role)
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<ProjectRoleResponse>>(true, "Project roles retrieved.", items));
    }

    /// <summary>
    /// Grant a user a role on this project, replacing any project-scope role they already hold.
    /// Requires <c>project:member:manage</c>. Valid roles: ProjectAdmin, Tester, Contributor,
    /// Viewer — whatever <see cref="RolePermissions.GrantableToUsersAt"/> permits, which is the
    /// list this endpoint validates against rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// This is how a member of the organization gains access to a project's work items —
    /// organization membership alone deliberately grants nothing inside a project.
    /// </remarks>
    [HttpPost("api/projects/{projectId:guid}/roles")]
    [RequirePermission(Permissions.ProjectMemberManage, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<ProjectRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRole(
        Guid projectId,
        [FromBody] AssignProjectRoleRequest request,
        CancellationToken ct)
    {

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

        // Staged before the assignment, not after: AssignRoleAsync saves the shared unit of work,
        // so enqueuing first is what makes the outbox row and the role change commit together.
        _eventBus.Enqueue(new ProjectRoleChanged(
            projectId, project.OrganizationId, project.Name, request.UserId, request.Role, _currentUser.UserId));

        var assignment = await _rbac.AssignRoleAsync(
            request.UserId, request.Role, RoleScope.Project, projectId, _currentUser.UserId, ct: ct);

        return Ok(new ApiResponse<ProjectRoleResponse>(true, $"Role updated to {request.Role}.",
            new ProjectRoleResponse(assignment.UserId, assignment.Role, assignment.CreatedAt)));
    }

    /// <summary>
    /// Revoke a user's project-scope role. Requires <c>project:member:manage</c>.
    /// The last remaining ProjectAdmin cannot be revoked.
    /// </summary>
    [HttpDelete("api/projects/{projectId:guid}/roles/{userId:guid}")]
    [RequirePermission(Permissions.ProjectMemberManage, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRole(Guid projectId, Guid userId, CancellationToken ct)
    {

        var existing = await _rbac.GetScopeRolesAsync(RoleScope.Project, projectId, ct);
        var held = existing.Where(r => r.UserId == userId).ToList();

        if (held.Count == 0)
            throw new NotFoundException($"User {userId} holds no role in this project.");

        // Refuse to strip the last ProjectAdmin — nobody could administer the project afterwards.
        if (held.Any(r => r.Role == RoleType.ProjectAdmin) &&
            existing.Count(r => r.Role == RoleType.ProjectAdmin) == 1)
            return BadRequest(new ApiResponse(false, "Cannot revoke the last ProjectAdmin of a project."));

        // Read the project and stage the event before the revocations — each RemoveRoleAsync saves
        // the shared unit of work, so the outbox row commits with the first of them rather than
        // being left stranded in the change tracker with nothing left to save it.
        var project = await _projectService.GetByIdAsync(projectId, ct);
        _eventBus.Enqueue(new ProjectRoleChanged(
            projectId, project.OrganizationId, project.Name, userId, null, _currentUser.UserId));

        foreach (var role in held)
            await _rbac.RemoveRoleAsync(userId, role.Role, RoleScope.Project, projectId, ct);

        return Ok(new ApiResponse(true, "Project role revoked."));
    }

    // -------------------------------------------------------------------------
    private async Task RequireOrgAsync(Guid orgId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Organization, orgId, ct))
            throw new ForbiddenException();
    }

    private async Task RequireProjectAsync(Guid projectId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Project, projectId, ct))
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
