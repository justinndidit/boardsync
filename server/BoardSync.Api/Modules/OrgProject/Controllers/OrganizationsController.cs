using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Services;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
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
    /// Roles assignable at organization scope — <c>OrgAdmin</c> and <c>Member</c>.
    /// </summary>
    /// <remarks>
    /// Read from the permission table rather than written out here, so this cannot drift from what
    /// the table and the check constraint actually accept. It did drift once: 'ProjectAdmin' and
    /// 'TeamMember' were assignable at organization scope and granted nothing beyond organization
    /// read, which made them a trap — an administrator would set someone to ProjectAdmin expecting
    /// authority over projects and hand them none. Project authority is a project-scope grant.
    /// </remarks>
    private static readonly IReadOnlyList<RoleType> AssignableOrgRoles =
        RolePermissions.AssignableAt(RoleScope.Organization);

    private readonly IOrganizationService _orgService;
    private readonly IOrganizationAvatarService _orgAvatars;
    private readonly IOrganizationInvitationService _invitations;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;
    private readonly IActivityQueryService _activity;

    public OrganizationsController(
        IOrganizationService orgService,
        IOrganizationAvatarService orgAvatars,
        IOrganizationInvitationService invitations,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        IActivityQueryService activity)
    {
        _orgService = orgService;
        _orgAvatars = orgAvatars;
        _invitations = invitations;
        _rbac = rbac;
        _currentUser = currentUser;
        _activity = activity;
    }

    /// <summary>Get all organizations the current user belongs to.</summary>
    [HttpGet]
    [NoPermissionRequired(
        "Returns only the caller\u0027s own organizations; the query is scoped to their memberships.")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<OrganizationSummaryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyOrgs([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _orgService.GetForUserAsync(_currentUser.UserId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<OrganizationSummaryResponse>>(true, "Organizations retrieved.", result));
    }

    /// <summary>Create a new organization. The caller automatically becomes OrgAdmin.</summary>
    [HttpPost]
    [NoPermissionRequired(
        "Creating an organization is self-service; the creator becomes its OrgAdmin.")]
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
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid orgId, CancellationToken ct)
    {
        var org = await _orgService.GetByIdAsync(orgId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization retrieved.", org));
    }

    /// <summary>Get organization by slug.</summary>
    [HttpGet("by-slug/{slug}")]
    [PermissionCheckedInAction(
        "Keyed on a slug, not a scope id — the organization must be resolved before it can be authorized.")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var org = await _orgService.GetBySlugAsync(slug, _currentUser.UserId, ct);
        await RequireOrgAsync(org.Id, Permissions.OrgRead, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization retrieved.", org));
    }

    /// <summary>Update organization details. Requires OrgAdmin.</summary>
    [HttpPut("{orgId:guid}")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid orgId, [FromBody] UpdateOrganizationRequest request, CancellationToken ct)
    {
        var org = await _orgService.UpdateAsync(orgId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<OrganizationResponse>(true, "Organization updated.", org));
    }

    /// <summary>
    /// Request a short-lived URL to upload a new organization logo to. Requires OrgAdmin.
    /// </summary>
    /// <remarks>
    /// Step one of two, and the same shape as a profile picture: the browser PUTs the file to the
    /// returned URL with the returned headers — and no bearer token, the URL carries its own
    /// credential — then calls <c>POST /orgs/{orgId}/avatar</c> with the returned
    /// <c>blobPath</c> to claim it. Nothing changes on the organization until that second call.
    /// </remarks>
    [HttpPost("{orgId:guid}/avatar/upload-url")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<AvatarUploadTicketResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateAvatarUploadUrl(
        Guid orgId, [FromBody] AvatarUploadTicketRequest request, CancellationToken ct)
    {
        if (!_orgAvatars.IsAvailable)
            return StorageUnavailable();

        var result = await _orgAvatars.CreateUploadTicketAsync(orgId, request, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Claim an uploaded image as the organization's logo. Requires OrgAdmin.
    /// </summary>
    /// <remarks>
    /// The API reads the blob's leading bytes back out of its own container and refuses anything
    /// that is not really an image, so what is stored is verified rather than asserted. The logo
    /// being replaced is deleted once the new one is saved.
    /// </remarks>
    [HttpPost("{orgId:guid}/avatar")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SetAvatar(
        Guid orgId, [FromBody] SetProfilePictureRequest request, CancellationToken ct)
    {
        if (!_orgAvatars.IsAvailable)
            return StorageUnavailable();

        var result = await _orgAvatars.CommitAsync(orgId, request, _currentUser.UserId, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Remove the organization's logo. Requires OrgAdmin.</summary>
    /// <remarks>
    /// Works whether or not storage is configured: clearing the field is a change to the
    /// organization record, and an administrator who wants a logo gone should not be told to wait
    /// for an integration. The blob is deleted too when there is a container to delete it from.
    /// </remarks>
    [HttpDelete("{orgId:guid}/avatar")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAvatar(Guid orgId, CancellationToken ct)
    {
        var result = await _orgAvatars.RemoveAsync(orgId, _currentUser.UserId, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Says the feature is switched off rather than broken.
    /// </summary>
    /// <remarks>
    /// 503 and not 500: a deployment without a storage connection string is a configuration
    /// choice, and the client's correct response is to hide the upload control — which it cannot
    /// decide to do if this looks like a transient server fault.
    /// </remarks>
    private ObjectResult StorageUnavailable() =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new ApiResponse(
                false,
                "Logo uploads are not configured on this deployment."));

    /// <summary>List all members of an organization with their roles. Requires <c>org:read</c>.</summary>
    [HttpGet("{orgId:guid}/members")]
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<OrgMemberResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers(Guid orgId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _orgService.GetMembersAsync(orgId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<OrgMemberResponse>>(true, "Members retrieved.", result));
    }

    /*
     * `POST /orgs/{orgId}/members` is gone.
     *
     * It took a user id and put that person into the organization immediately — no notice, no
     * consent, and nothing at all for somebody without an account, because the client had to
     * resolve the address through `GET /users/by-email` first and that answered 404. Membership is
     * offered now, and accepted by the person it is offered to: the three endpoints below.
     */

    /// <summary>
    /// Invite somebody to the organization by email. Requires <c>org:member:manage</c>.
    /// </summary>
    /// <remarks>
    /// The address does not need an account. The invitation is emailed either way; the recipient
    /// signs in, or signs up with that address, and accepts. An invitation still open for the same
    /// address is superseded, so re-inviting cannot leave two working links to one membership.
    /// </remarks>
    /// <response code="200">Invitation created and emailed</response>
    /// <response code="400">Already a member, or a role that cannot be granted at organization scope</response>
    [HttpPost("{orgId:guid}/invitations")]
    [RequirePermission(Permissions.OrgMemberManage, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationInvitationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InviteMember(
        Guid orgId, [FromBody] InviteMemberRequest request, CancellationToken ct)
    {
        var result = await _invitations.InviteAsync(orgId, request, _currentUser.UserId, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// List this organization's invitations. Requires <c>org:member:manage</c>.
    /// </summary>
    /// <remarks>
    /// Restricted to people who can manage members rather than to <c>org:read</c>: who has been
    /// invited and has not yet joined is administrative detail, and an ordinary member has no call
    /// to read a list of addresses.
    /// </remarks>
    /// <param name="orgId">The organization.</param>
    /// <param name="openOnly">Only invitations that can still be accepted. Default true.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{orgId:guid}/invitations")]
    [RequirePermission(Permissions.OrgMemberManage, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OrganizationInvitationResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvitations(
        Guid orgId, [FromQuery] bool openOnly, CancellationToken ct)
    {
        var result = await _invitations.ListAsync(orgId, openOnly, ct);

        return Ok(new ApiResponse<IReadOnlyList<OrganizationInvitationResponse>>(
            true, "Invitations retrieved.", result));
    }

    /// <summary>Withdraw an invitation. Requires <c>org:member:manage</c>.</summary>
    /// <remarks>
    /// Only before it is accepted. Once somebody has joined, revoking would say they are not a
    /// member while they are — removing a member is a different act, with its own endpoint and its
    /// own protection for the last OrgAdmin.
    /// </remarks>
    [HttpDelete("{orgId:guid}/invitations/{invitationId:guid}")]
    [RequirePermission(Permissions.OrgMemberManage, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvitation(
        Guid orgId, Guid invitationId, CancellationToken ct)
    {
        var result = await _invitations.RevokeAsync(orgId, invitationId, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Remove a user from the organization. Requires OrgAdmin.</summary>
    [HttpDelete("{orgId:guid}/members/{userId:guid}")]
    [RequirePermission(Permissions.OrgMemberManage, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveMember(Guid orgId, Guid userId, CancellationToken ct)
    {
        await _orgService.RemoveMemberAsync(orgId, userId, _currentUser.UserId, ct);
        return Ok(new ApiResponse(true, "Member removed from organization."));
    }

    /// <summary>
    /// Update a member's role within this organization. Requires OrgAdmin.
    /// Valid roles: OrgAdmin, Member.
    /// </summary>
    [HttpPut("{orgId:guid}/members/{userId:guid}/role")]
    [RequirePermission(Permissions.OrgMemberManage, From = "orgId")]
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
    /// sprint and board changes, plus membership and role changes. Requires <c>org:read</c>, which every
    /// organization member holds — membership always carries at least that role.
    /// </summary>
    /// <remarks>
    /// Reads the same activity log as <c>/api/workspace/activity</c>; that endpoint simply spans
    /// every organization the caller belongs to instead of one.
    /// </remarks>
    [HttpGet("{orgId:guid}/activity")]
    [RequirePermission(Permissions.OrgRead, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ActivityResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActivity(
        Guid orgId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {

        var result = await _activity.GetForOrganizationsAsync([orgId], pagination, ct);

        return Ok(new ApiResponse<PagedResult<ActivityResponse>>(true, "Activity retrieved.", result));
    }

    private async Task RequireOrgAsync(Guid orgId, string permission, CancellationToken ct)
    {
        var permitted = await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Organization, orgId, ct);
        if (!permitted)
            throw new ForbiddenException();
    }
}

/// <summary>Request body for updating a member's org-level role.</summary>
public record UpdateMemberRoleRequest(
    [Required] RoleType Role
);
