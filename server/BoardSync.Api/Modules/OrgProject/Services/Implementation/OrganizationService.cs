using BoardSync.Api.Modules.OrgProject.Domain.Helpers;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

public class OrganizationService : IOrganizationService
{
    private readonly IOrganizationRepository _organizationRepo;
    private readonly IRbacService _rbac;
    private readonly IUserService _userService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(
        IOrganizationRepository organizationRepository,
        IRbacService rbac,
        IUserService userService,
        IEventBus eventBus,
        ILogger<OrganizationService> logger)
    {
        _organizationRepo = organizationRepository;
        _rbac = rbac;
        _userService = userService;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<OrganizationResponse> CreateAsync(
        CreateOrganizationRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var slug = Slug.From(request.Slug ?? request.Name);

        if (await _organizationRepo.SlugExistsAsync(slug, ct))
            throw new ConflictException($"An organization with slug '{slug}' already exists.");

        var org = new Organization
        {
            Slug = slug,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        // The organization, its owner's membership and the owner's OrgAdmin role must all land or
        // none of them: an organization with no OrgAdmin can never be administered again.
        await _organizationRepo.ExecuteInTransactionAsync(async token =>
        {
            _organizationRepo.Add(org);
            _organizationRepo.AddMembership(new OrganizationMembership
            {
                OrganizationId = org.Id,
                UserId = createdBy,
                CreatedBy = createdBy
            });
            await _organizationRepo.SaveChangesAsync(token);

            // Creator becomes OrgAdmin automatically. Saves through the RBAC module's own service,
            // which enlists in the transaction opened above.
            await _rbac.AssignRoleAsync(createdBy, RoleType.OrgAdmin, RoleScope.Organization, org.Id, createdBy, token);
        }, ct);

        await _eventBus.PublishAsync(new OrganizationCreated(org.Id, org.Name, org.Slug, createdBy), ct);

        _logger.LogInformation("Organization '{Name}' ({Id}) created by {UserId}", org.Name, org.Id, createdBy);

        var counts = await _organizationRepo.GetCountsAsync(org.Id, ct);
        var userRole = await ResolveUserRoleAsync(createdBy, org.Id, ct);
        return MapToResponse(org, counts, userRole);
    }

    public async Task<OrganizationResponse> GetByIdAsync(Guid orgId, Guid requestingUserId, CancellationToken ct = default)
    {
        var org = await _organizationRepo.GetActiveAsync(orgId, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        var counts = await _organizationRepo.GetCountsAsync(orgId, ct);
        var userRole = await ResolveUserRoleAsync(requestingUserId, orgId, ct);
        return MapToResponse(org, counts, userRole);
    }

    public async Task<OrganizationResponse> GetBySlugAsync(string slug, Guid requestingUserId, CancellationToken ct = default)
    {
        var org = await _organizationRepo.GetActiveBySlugAsync(slug.Trim().ToLowerInvariant(), ct)
            ?? throw new NotFoundException($"Organization with slug '{slug}' was not found.");

        var counts = await _organizationRepo.GetCountsAsync(org.Id, ct);
        var userRole = await ResolveUserRoleAsync(requestingUserId, org.Id, ct);
        return MapToResponse(org, counts, userRole);
    }

    public async Task<PagedResult<OrganizationSummaryResponse>> GetForUserAsync(
        Guid userId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var (records, total) = await _organizationRepo.GetForUserAsync(
            userId, pagination.Skip, pagination.PageSize, ct);

        // One query for every role this user holds; filtered to org scope in memory.
        var assignments = await _rbac.GetUserRolesAsync(userId, ct);
        var roleMap = MostPrivilegedBy(
            assignments.Where(ra => ra.Scope == RoleScope.Organization),
            ra => ra.OrganizationId!.Value);

        var items = records.Select(o => new OrganizationSummaryResponse(
            o.Id, o.Slug, o.Name, o.AvatarUrl, o.Description, o.IsActive,
            o.MemberCount, o.ProjectCount, o.CreatedAt, RoleNameFor(roleMap, o.Id))).ToList();

        return new PagedResult<OrganizationSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<OrganizationResponse> UpdateAsync(
        Guid orgId,
        UpdateOrganizationRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var org = await _organizationRepo.GetActiveAsync(orgId, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        org.Name = request.Name.Trim();
        org.Description = request.Description?.Trim() ?? org.Description;
        org.AvatarUrl = request.AvatarUrl ?? org.AvatarUrl;
        org.UpdatedAt = DateTime.UtcNow;

        await _organizationRepo.SaveChangesAsync(ct);

        var counts = await _organizationRepo.GetCountsAsync(orgId, ct);
        var userRole = await ResolveUserRoleAsync(updatedBy, orgId, ct);
        return MapToResponse(org, counts, userRole);
    }

    public async Task AddMemberAsync(Guid orgId, Guid userId, Guid addedBy, CancellationToken ct = default)
    {
        if (!await _organizationRepo.ExistsActiveAsync(orgId, ct))
            throw new NotFoundException(nameof(Organization), orgId);

        var user = await _userService.GetByIdAsync(userId);
        if (!user.Success || user.Data is null)
            throw new NotFoundException("User", userId);

        await _organizationRepo.ExecuteInTransactionAsync(async token =>
        {
            if (!await _organizationRepo.IsMemberAsync(orgId, userId, token))
            {
                _organizationRepo.AddMembership(new OrganizationMembership
                {
                    OrganizationId = orgId,
                    UserId = userId,
                    CreatedBy = addedBy
                });
                await _organizationRepo.SaveChangesAsync(token);
            }

            if (!await _rbac.HasRoleAsync(userId, RoleType.Reader, RoleScope.Organization, orgId, token))
                await _rbac.AssignRoleAsync(userId, RoleType.Reader, RoleScope.Organization, orgId, addedBy, token);
        }, ct);

        await _eventBus.PublishAsync(new MemberAddedToOrg(orgId, userId, addedBy), ct);
    }

    public async Task RemoveMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _organizationRepo.GetMembershipAsync(orgId, userId, ct);
        if (membership is null) return;

        await _organizationRepo.ExecuteInTransactionAsync(async token =>
        {
            _organizationRepo.RemoveMembership(membership);
            await _organizationRepo.SaveChangesAsync(token);

            await _rbac.RemoveAllRolesAsync(userId, RoleScope.Organization, orgId, token);
        }, ct);

        _logger.LogInformation("User {UserId} removed from organization {OrgId}", userId, orgId);
    }

    public async Task SetMemberRoleAsync(
        Guid orgId,
        Guid userId,
        RoleType role,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        if (!await _organizationRepo.IsMemberAsync(orgId, userId, ct))
            throw new NotFoundException($"User {userId} is not a member of this organization.");

        // Read the current roles, check the demotion guard and swap, all inside one transaction.
        // Doing the swap as two independent saves is what previously let a cancelled or failed
        // request strand a member with no org role at all, and let two concurrent updates each
        // remove one role and add another, leaving the member holding two.
        await _organizationRepo.ExecuteInTransactionAsync(async token =>
        {
            var orgRoles = await _rbac.GetScopeRolesAsync(RoleScope.Organization, orgId, token);
            var held = orgRoles.Where(ra => ra.UserId == userId).Select(ra => ra.Role).ToList();

            // Refuse to demote the last OrgAdmin — the organization would become unmanageable, and
            // OrgAdmin is the only role that can hand out organization roles in the first place.
            if (held.Contains(RoleType.OrgAdmin) &&
                role != RoleType.OrgAdmin &&
                orgRoles.Count(ra => ra.Role == RoleType.OrgAdmin) == 1)
                throw new DomainException("Cannot demote the last OrgAdmin of an organization.");

            // Clear every org-scope role rather than just the one we happened to read first:
            // nothing in the schema limits a user to a single role per organization, and leaving a
            // stale one behind would keep granting the privileges we were asked to take away.
            await _rbac.RemoveAllRolesAsync(userId, RoleScope.Organization, orgId, token);
            await _rbac.AssignRoleAsync(userId, role, RoleScope.Organization, orgId, actingUserId, token);
        }, ct);

        _logger.LogInformation("Org {OrgId} role for user {UserId} set to {Role} by {ActingUserId}",
            orgId, userId, role, actingUserId);
    }

    public Task<bool> IsMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default) =>
        _organizationRepo.IsMemberAsync(orgId, userId, ct);

    public async Task<PagedResult<OrgMemberResponse>> GetMembersAsync(
        Guid orgId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (!await _organizationRepo.ExistsActiveAsync(orgId, ct))
            throw new NotFoundException(nameof(Organization), orgId);

        var (members, total) = await _organizationRepo.GetMembersAsync(
            orgId, pagination.Skip, pagination.PageSize, ct);

        // One query for every org-scope role in this organization; matched to members in memory.
        var assignments = await _rbac.GetScopeRolesAsync(RoleScope.Organization, orgId, ct);
        var roleMap = MostPrivilegedBy(assignments, ra => ra.UserId);
        var items = members.Select(m => new OrgMemberResponse(
            m.UserId, m.DisplayName, m.Email, m.ProfilePictureUrl,
            RoleNameFor(roleMap, m.UserId), m.JoinedAt)).ToList();

        return new PagedResult<OrgMemberResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    // -------------------------------------------------------------------------

    private static OrganizationResponse MapToResponse(Organization org, OrganizationCounts counts, string userRole) =>
        new(org.Id, org.Slug, org.Name, org.Description, org.AvatarUrl, org.IsActive,
            counts.MemberCount, counts.ProjectCount, org.CreatedAt, userRole);

    private async Task<string> ResolveUserRoleAsync(Guid userId, Guid orgId, CancellationToken ct)
    {
        var assignments = await _rbac.GetUserRolesAsync(userId, ct);

        var role = assignments
            .Where(ra => ra.Scope == RoleScope.Organization && ra.OrganizationId == orgId)
            .OrderBy(ra => (int)ra.Role) // lowest value = most privileged
            .FirstOrDefault()?.Role;

        return role?.ToString() ?? "None";
    }

    /// <summary>
    /// Reduces role assignments to the single most-privileged role per key (lowest enum value wins).
    /// </summary>
    private static Dictionary<Guid, RoleType> MostPrivilegedBy(
        IEnumerable<RoleAssignment> assignments,
        Func<RoleAssignment, Guid> keySelector) =>
        assignments
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Min(ra => ra.Role));

    private static string RoleNameFor(IReadOnlyDictionary<Guid, RoleType> roleMap, Guid key) =>
        roleMap.TryGetValue(key, out var role) ? role.ToString() : "None";
}
