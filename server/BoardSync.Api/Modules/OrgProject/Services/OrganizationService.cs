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

        var org = new Organization
        {
            Slug = slug,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _context.Organizations.Add(org);
        await _context.SaveChangesAsync(ct);

        // Creator becomes OrgAdmin automatically
        await _rbac.AssignRoleAsync(createdBy, RoleType.OrgAdmin, RoleScope.Organization, org.Id, createdBy, ct);

        // Add to org membership
        _context.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = org.Id,
            UserId = createdBy,
            CreatedBy = createdBy
        });
        await _context.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(new OrganizationCreated(org.Id, org.Name, org.Slug, createdBy), ct);

        _logger.LogInformation("Organization '{Name}' ({Id}) created by {UserId}", org.Name, org.Id, createdBy);

        return await MapToResponseAsync(org, ct);
    }

    public async Task<OrganizationResponse> GetByIdAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Projects)
            .Include(o => o.Members)
            .FirstOrDefaultAsync(o => o.Id == orgId && o.IsActive, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        return MapToResponse(org);
    }

    public async Task<OrganizationResponse> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Projects)
            .Include(o => o.Members)
            .FirstOrDefaultAsync(o => o.Slug == slug.ToLowerInvariant() && o.IsActive, ct)
            ?? throw new NotFoundException($"Organization with slug '{slug}' was not found.");

        return MapToResponse(org);
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
        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(o => new OrganizationSummaryResponse(o.Id, o.Slug, o.Name, o.AvatarUrl))
            .ToListAsync(ct);

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
        return MapToResponse(org);
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

    // -------------------------------------------------------------------------
    private static string NormalizeSlug(string input)
    {
        return System.Text.RegularExpressions.Regex
            .Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
    }

    private static OrganizationResponse MapToResponse(Organization org) =>
        new(org.Id, org.Slug, org.Name, org.Description, org.AvatarUrl, org.IsActive,
            org.Members.Count, org.Projects.Count, org.CreatedAt);

    private async Task<OrganizationResponse> MapToResponseAsync(Organization org, CancellationToken ct)
    {
        var memberCount = await _context.OrganizationMemberships.CountAsync(m => m.OrganizationId == org.Id, ct);
        var projectCount = await _context.Projects.CountAsync(p => p.OrganizationId == org.Id && p.IsActive, ct);
        return new(org.Id, org.Slug, org.Name, org.Description, org.AvatarUrl, org.IsActive,
            memberCount, projectCount, org.CreatedAt);
    }
}
