using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;

public interface IOrganizationService
{
    Task<OrganizationResponse> CreateAsync(CreateOrganizationRequest request, Guid createdBy, CancellationToken ct = default);
    Task<OrganizationResponse> GetByIdAsync(Guid orgId, Guid requestingUserId, CancellationToken ct = default);
    Task<OrganizationResponse> GetBySlugAsync(string slug, Guid requestingUserId, CancellationToken ct = default);
    Task<PagedResult<OrganizationSummaryResponse>> GetForUserAsync(Guid userId, PaginationQuery pagination, CancellationToken ct = default);
    Task<OrganizationResponse> UpdateAsync(Guid orgId, UpdateOrganizationRequest request, Guid updatedBy, CancellationToken ct = default);
    /// <summary>
    /// Puts a user into an organization with an organization-scope role.
    /// </summary>
    /// <remarks>
    /// No longer reachable from an endpoint. Membership is granted by accepting an invitation —
    /// see <see cref="IOrganizationInvitationService"/> — and this is the step that runs once
    /// somebody has. It used to be <c>POST /orgs/{orgId}/members</c>, which added a person to an
    /// organization without telling them.
    /// </remarks>
    Task AddMemberAsync(
        Guid orgId, Guid userId, Guid addedBy,
        RoleType role = RoleType.Member, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid orgId, Guid userId, Guid removedBy, CancellationToken ct = default);

    /// <summary>
    /// Replaces a member's organization-scope role with <paramref name="role"/>, as a single
    /// transaction: the member is never left role-less part way through, because losing their last
    /// org role would cut off everything membership is supposed to grant — the activity feed
    /// included. Refuses to demote the organization's last OrgAdmin.
    /// </summary>
    Task SetMemberRoleAsync(Guid orgId, Guid userId, RoleType role, Guid actingUserId, CancellationToken ct = default);
    Task<bool> IsMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default);
    Task<PagedResult<OrgMemberResponse>> GetMembersAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);
}