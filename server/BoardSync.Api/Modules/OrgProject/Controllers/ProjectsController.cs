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
/// Manage projects within an organization.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public ProjectsController(
        IProjectService projectService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _projectService = projectService;
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

    /// <summary>Create a new project within an organization. Requires OrgAdmin or ProjectAdmin.</summary>
    [HttpPost("api/orgs/{orgId:guid}/projects")]
    [ProducesResponseType(typeof(ApiResponse<ProjectResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid orgId, [FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.ProjectAdmin, ct);
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
