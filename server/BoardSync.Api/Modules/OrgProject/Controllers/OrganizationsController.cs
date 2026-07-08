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
using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// Manage organizations (top-level tenant containers).
/// </summary>
[ApiController]
[Route("api/orgs")]
[Authorize]
[Produces("application/json")]
public class OrganizationsController : ControllerBase
{
    private readonly IOrganizationService _orgService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public OrganizationsController(
        IOrganizationService orgService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _orgService = orgService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>Get all organizations the current user belongs to.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<OrganizationSummaryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyOrgs([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _orgService.GetForUserAsync(_currentUser.UserId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<OrganizationSummaryResponse>>(true, "Organizations retrieved.", result));
    }

    /// <summary>Create a new organization. The caller automatically becomes OrgAdmin.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        var org = await _orgService.CreateAsync(request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { orgId = org.Id },
            new ApiResponse<OrganizationResponse>(true, "Organization created.", org));
    }

    /// <summary>Get organization by ID.</summary>
    [HttpGet("{orgId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid orgId, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.Reader, ct);
        var org = await _orgService.GetByIdAsync(orgId, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization retrieved.", org));
    }

    /// <summary>Get organization by slug.</summary>
    [HttpGet("by-slug/{slug}")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var org = await _orgService.GetBySlugAsync(slug, ct);
        await RequireOrgRoleAsync(org.Id, RoleType.Reader, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization retrieved.", org));
    }

    /// <summary>Update organization details. Requires OrgAdmin.</summary>
    [HttpPut("{orgId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid orgId, [FromBody] UpdateOrganizationRequest request, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);
        var org = await _orgService.UpdateAsync(orgId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization updated.", org));
    }

    /// <summary>Add a user to the organization. Requires OrgAdmin.</summary>
    [HttpPost("{orgId:guid}/members")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMember(Guid orgId, [FromBody] AddTeamMemberRequest request, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);
        await _orgService.AddMemberAsync(orgId, request.UserId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Member added to organization."));
    }

    /// <summary>Remove a user from the organization. Requires OrgAdmin.</summary>
    [HttpDelete("{orgId:guid}/members/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid orgId, Guid userId, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);
        await _orgService.RemoveMemberAsync(orgId, userId, ct);
        return Ok(new ApiResponse(true, "Member removed from organization."));
    }

    /// <summary>
    /// Update a member's role within this organization. Requires OrgAdmin.
    /// Valid roles: OrgAdmin, ProjectAdmin, TeamMember, Reader.
    /// </summary>
    [HttpPut("{orgId:guid}/members/{userId:guid}/role")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMemberRole(
        Guid orgId,
        Guid userId,
        [FromBody] UpdateMemberRoleRequest request,
        CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.OrgAdmin, ct);

        // Validate the target user is actually a member
        var isMember = await _orgService.IsMemberAsync(orgId, userId, ct);
        if (!isMember)
            throw new NotFoundException($"User {userId} is not a member of this organization.");

        // Remove any existing org-scope role for this user and reassign
        var existingRoles = await _rbac.GetScopeRolesAsync(RoleScope.Organization, orgId, ct);
        var currentOrgRole = existingRoles.FirstOrDefault(r => r.UserId == userId);

        if (currentOrgRole != null)
            await _rbac.RemoveRoleAsync(userId, currentOrgRole.Role, RoleScope.Organization, orgId, ct);

        await _rbac.AssignRoleAsync(userId, request.Role, RoleScope.Organization, orgId, _currentUser.UserId, ct);

        return Ok(new ApiResponse(true, $"Role updated to {request.Role}."));
    }

    // -------------------------------------------------------------------------
    private async Task RequireOrgRoleAsync(Guid orgId, RoleType minimum, CancellationToken ct)
    {
        var permitted = await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Organization, orgId, ct);
        if (!permitted)
            throw new ForbiddenException();
    }
}

/// <summary>Request body for updating a member's org-level role.</summary>
public record UpdateMemberRoleRequest(
    [Required] RoleType Role
);
