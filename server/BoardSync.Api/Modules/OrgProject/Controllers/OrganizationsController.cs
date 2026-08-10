using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Services;
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
    /// <summary>
    /// Roles assignable at organization scope. User is the "no permissions yet" default carried by
    /// every authenticated account and is never granted explicitly.
    /// </summary>
    private static readonly RoleType[] AssignableOrgRoles =
        [RoleType.OrgAdmin, RoleType.ProjectAdmin, RoleType.TeamMember, RoleType.Reader];

    private readonly IOrganizationService _orgService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;
    private readonly IActivityQueryService _activity;

    public OrganizationsController(
        IOrganizationService orgService,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        IActivityQueryService activity)
    {
        _orgService = orgService;
        _rbac = rbac;
        _currentUser = currentUser;
        _activity = activity;
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
        var org = await _orgService.GetByIdAsync(orgId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization retrieved.", org));
    }

    /// <summary>Get organization by slug.</summary>
    [HttpGet("by-slug/{slug}")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var org = await _orgService.GetBySlugAsync(slug, _currentUser.UserId, ct);
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

    /// <summary>List all members of an organization with their roles. Requires Reader.</summary>
    [HttpGet("{orgId:guid}/members")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<OrgMemberResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.Reader, ct);
        var result = await _orgService.GetMembersAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<OrgMemberResponse>>(true, "Members retrieved.", result));
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
        await _orgService.RemoveMemberAsync(orgId, userId, _currentUser.UserId, ct);
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

        if (!AssignableOrgRoles.Contains(request.Role))
            return BadRequest(new ApiResponse(false,
                $"'{request.Role}' cannot be assigned at organization scope. Valid roles: {string.Join(", ", AssignableOrgRoles)}."));

        // Membership check, last-OrgAdmin guard and the role swap all belong to one transaction,
        // so they live together in the service rather than being sequenced from here.
        await _orgService.SetMemberRoleAsync(orgId, userId, request.Role, _currentUser.UserId, ct);

        return Ok(new ApiResponse(true, $"Role updated to {request.Role}."));
    }

    /// <summary>
    /// Everything that has happened in this organization, newest first: work item, project, team,
    /// sprint and board changes, plus membership and role changes. Requires Reader, which every
    /// organization member holds — membership always carries at least that role.
    /// </summary>
    /// <remarks>
    /// Reads the same activity log as <c>/api/workspace/activity</c>; that endpoint simply spans
    /// every organization the caller belongs to instead of one.
    /// </remarks>
    [HttpGet("{orgId:guid}/activity")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ActivityResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActivity(
        Guid orgId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        await RequireOrgRoleAsync(orgId, RoleType.Reader, ct);

        var result = await _activity.GetForOrganizationsAsync([orgId], pagination, ct);

        return Ok(new ApiResponse<PagedResult<ActivityResponse>>(true, "Activity retrieved.", result));
    }

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
