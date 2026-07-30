using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.DTOs;
using BoardSync.Api.Modules.OrgProject.Events;
using BoardSync.Api.Modules.OrgProject.Models;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Shared.Auth.Models;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Services;

public class OrganizationService : IOrganizationService
{
    private readonly BoardSyncDbContext _context;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(
        BoardSyncDbContext context,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<OrganizationService> logger)
    {
        _context = context;
        _rbac = rbac;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<OrganizationResponse> CreateAsync(
        CreateOrganizationRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var slug = NormalizeSlug(request.Slug ?? request.Name);

        if (await _context.Organizations.AnyAsync(o => o.Slug == slug, ct))
            throw new ConflictException($"An organization with slug '{slug}' already exists.");

        // EF Core's retry strategy cannot span multiple SaveChangesAsync calls unless
        // we wrap the whole operation in ExecuteAsync — required when EnableRetryOnFailure is set.
        var strategy = _context.Database.CreateExecutionStrategy();

        Organization org = null!;

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            org = new Organization
            {
                Slug = slug,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedBy = createdBy
            };

            _context.Organizations.Add(org);
            await _context.SaveChangesAsync(ct);

            // Creator becomes OrgAdmin automatically
            var assignment = new RoleAssignment
            {
                UserId = createdBy,
                Role = RoleType.OrgAdmin,
                Scope = RoleScope.Organization,
                ScopeId = org.Id,
                CreatedBy = createdBy
            };
            _context.RoleAssignments.Add(assignment);

            // Add to org membership
            _context.OrganizationMemberships.Add(new OrganizationMembership
            {
                OrganizationId = org.Id,
                UserId = createdBy,
                CreatedBy = createdBy
            });

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        await _eventBus.PublishAsync(new OrganizationCreated(org.Id, org.Name, org.Slug, createdBy), ct);

        _logger.LogInformation("Organization '{Name}' ({Id}) created by {UserId}", org.Name, org.Id, createdBy);

        return await MapToResponseAsync(org, createdBy, ct);
    }

    public async Task<OrganizationResponse> GetByIdAsync(Guid orgId, Guid requestingUserId, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Projects)
            .Include(o => o.Members)
            .FirstOrDefaultAsync(o => o.Id == orgId && o.IsActive, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        var userRole = await ResolveUserRoleAsync(requestingUserId, orgId, ct);
        return MapToResponse(org, userRole);
    }

    public async Task<OrganizationResponse> GetBySlugAsync(string slug, Guid requestingUserId, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Projects)
            .Include(o => o.Members)
            .FirstOrDefaultAsync(o => o.Slug == slug.ToLowerInvariant() && o.IsActive, ct)
            ?? throw new NotFoundException($"Organization with slug '{slug}' was not found.");

        var userRole = await ResolveUserRoleAsync(requestingUserId, org.Id, ct);
        return MapToResponse(org, userRole);
    }

    public async Task<PagedResult<OrganizationSummaryResponse>> GetForUserAsync(
        Guid userId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var query = _context.Organizations
            .Where(o => o.IsActive && o.Members.Any(m => m.UserId == userId))
            .OrderBy(o => o.Name);

        var total = await query.CountAsync(ct);
        var orgs = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(o => new
            {
                o.Id, o.Slug, o.Name, o.AvatarUrl, o.Description,
                o.IsActive, o.CreatedAt,
                MemberCount = o.Members.Count,
                ProjectCount = o.Projects.Count(p => p.IsActive)
            })
            .ToListAsync(ct);

        // Batch-load calling user's org-scope roles in one query
        var orgIds = orgs.Select(o => o.Id).ToList();
        var roleMap = await _context.RoleAssignments
            .Where(ra => ra.UserId == userId
                         && ra.Scope == RoleScope.Organization
                         && orgIds.Contains(ra.ScopeId))
            .ToDictionaryAsync(ra => ra.ScopeId, ra => ra.Role, ct);

        var items = orgs.Select(o =>
        {
            var role = roleMap.TryGetValue(o.Id, out var r) ? r.ToString() : "None";
            return new OrganizationSummaryResponse(
                o.Id, o.Slug, o.Name, o.AvatarUrl, o.Description,
                o.IsActive, o.MemberCount, o.ProjectCount, o.CreatedAt, role);
        }).ToList();

        return new PagedResult<OrganizationSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<OrganizationResponse> UpdateAsync(
        Guid orgId,
        UpdateOrganizationRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Projects)
            .Include(o => o.Members)
            .FirstOrDefaultAsync(o => o.Id == orgId && o.IsActive, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        org.Name = request.Name.Trim();
        org.Description = request.Description?.Trim() ?? org.Description;
        org.AvatarUrl = request.AvatarUrl ?? org.AvatarUrl;
        org.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        var userRole = await ResolveUserRoleAsync(updatedBy, orgId, ct);
        return MapToResponse(org, userRole);
    }

    public async Task AddMemberAsync(Guid orgId, Guid userId, Guid addedBy, CancellationToken ct = default)
    {
        if (!await _context.Organizations.AnyAsync(o => o.Id == orgId && o.IsActive, ct))
            throw new NotFoundException(nameof(Organization), orgId);

        if (!await _context.Users.AnyAsync(u => u.Id == userId, ct))
            throw new NotFoundException(nameof(User), userId);

        var exists = await _context.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId, ct);

        if (!exists)
        {
            _context.OrganizationMemberships.Add(new OrganizationMembership
            {
                OrganizationId = orgId,
                UserId = userId,
                CreatedBy = addedBy
            });
            await _context.SaveChangesAsync(ct);
        }

        // Assign default Reader role if they don't already have one
        var hasRole = await _rbac.HasRoleAsync(userId, RoleType.Reader, RoleScope.Organization, orgId, ct);
        if (!hasRole)
            await _rbac.AssignRoleAsync(userId, RoleType.Reader, RoleScope.Organization, orgId, addedBy, ct);

        await _eventBus.PublishAsync(new MemberAddedToOrg(orgId, userId, addedBy), ct);
    }

    public async Task RemoveMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _context.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == userId, ct);

        if (membership != null)
        {
            _context.OrganizationMemberships.Remove(membership);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> IsMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        return await _context.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId, ct);
    }

    public async Task<PagedResult<OrgMemberResponse>> GetMembersAsync(
        Guid orgId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (!await _context.Organizations.AnyAsync(o => o.Id == orgId && o.IsActive, ct))
            throw new NotFoundException(nameof(Organization), orgId);

        var query = _context.OrganizationMemberships
            .Where(m => m.OrganizationId == orgId)
            .OrderBy(m => m.JoinedAt);

        var total = await query.CountAsync(ct);

        // Fetch memberships with user data in one query
        var memberships = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Join(_context.Users,
                m => m.UserId,
                u => u.Id,
                (m, u) => new
                {
                    m.UserId,
                    u.DisplayName,
                    u.Email,
                    u.ProfilePictureUrl,
                    m.JoinedAt
                })
            .ToListAsync(ct);

        // Batch-load org-scope role assignments for these members in one query,
        // then pick the most-privileged role per user in memory (avoids (int) cast in SQL).
        var userIds = memberships.Select(m => m.UserId).ToList();
        var roleRows = await _context.RoleAssignments
            .Where(ra => ra.Scope == RoleScope.Organization
                         && ra.ScopeId == orgId
                         && userIds.Contains(ra.UserId))
            .Select(ra => new { ra.UserId, ra.Role })
            .ToListAsync(ct);

        var roleMap = roleRows
            .GroupBy(ra => ra.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(ra => (int)ra.Role).First().Role);

        var items = memberships.Select(m =>
        {
            var role = roleMap.TryGetValue(m.UserId, out var r) ? r.ToString() : "None";
            return new OrgMemberResponse(
                m.UserId, m.DisplayName, m.Email, m.ProfilePictureUrl, role, m.JoinedAt);
        }).ToList();

        return new PagedResult<OrgMemberResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    // -------------------------------------------------------------------------
    private static string NormalizeSlug(string input)
    {
        return System.Text.RegularExpressions.Regex
            .Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
    }

    private static OrganizationResponse MapToResponse(Organization org, string userRole) =>
        new(org.Id, org.Slug, org.Name, org.Description, org.AvatarUrl, org.IsActive,
            org.Members.Count, org.Projects.Count, org.CreatedAt, userRole);

    private async Task<OrganizationResponse> MapToResponseAsync(Organization org, Guid requestingUserId, CancellationToken ct)
    {
        var memberCount = await _context.OrganizationMemberships.CountAsync(m => m.OrganizationId == org.Id, ct);
        var projectCount = await _context.Projects.CountAsync(p => p.OrganizationId == org.Id && p.IsActive, ct);
        var userRole = await ResolveUserRoleAsync(requestingUserId, org.Id, ct);
        return new(org.Id, org.Slug, org.Name, org.Description, org.AvatarUrl, org.IsActive,
            memberCount, projectCount, org.CreatedAt, userRole);
    }

    private async Task<string> ResolveUserRoleAsync(Guid userId, Guid orgId, CancellationToken ct)
    {
        // Pull all role assignments for this user/org into memory, then pick the
        // most-privileged one in C# — (int) cast cannot be translated against varchar column.
        var roles = await _context.RoleAssignments
            .Where(ra => ra.UserId == userId
                         && ra.Scope == RoleScope.Organization
                         && ra.ScopeId == orgId)
            .Select(ra => ra.Role)
            .ToListAsync(ct);

        if (roles.Count == 0) return "None";

        var best = roles.OrderBy(r => (int)r).First();
        return best.ToString();
    }
}
