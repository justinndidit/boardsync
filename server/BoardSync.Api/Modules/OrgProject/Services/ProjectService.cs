using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.DTOs;
using BoardSync.Api.Modules.OrgProject.Events;
using BoardSync.Api.Modules.OrgProject.Models;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Services;

public class ProjectService : IProjectService
{
    private readonly BoardSyncDbContext _context;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        BoardSyncDbContext context,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<ProjectService> logger)
    {
        _context = context;
        _rbac = rbac;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<ProjectResponse> CreateAsync(
        Guid orgId,
        CreateProjectRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _context.Organizations.AnyAsync(o => o.Id == orgId && o.IsActive, ct))
            throw new NotFoundException("Organization", orgId);

        var slug = NormalizeSlug(request.Slug ?? request.Name);

        if (await _context.Projects.AnyAsync(p => p.OrganizationId == orgId && p.Slug == slug, ct))
            throw new ConflictException($"A project with slug '{slug}' already exists in this organization.");

        var project = new Project
        {
            OrganizationId = orgId,
            Slug = slug,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync(ct);

        // Creator becomes ProjectAdmin
        await _rbac.AssignRoleAsync(createdBy, RoleType.ProjectAdmin, RoleScope.Project, project.Id, createdBy, ct);

        await _eventBus.PublishAsync(new ProjectCreated(project.Id, orgId, project.Name, project.Slug, createdBy), ct);

        _logger.LogInformation("Project '{Name}' ({Id}) created in org {OrgId} by {UserId}",
            project.Name, project.Id, orgId, createdBy);

        return await MapToResponseAsync(project, ct);
    }

    public async Task<ProjectResponse> GetByIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _context.Projects
            .Include(p => p.Teams)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        return MapToResponse(project);
    }

    public async Task<PagedResult<ProjectSummaryResponse>> GetForOrgAsync(
        Guid orgId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var query = _context.Projects
            .Where(p => p.OrganizationId == orgId && p.IsActive)
            .OrderBy(p => p.Name);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(p => new ProjectSummaryResponse(p.Id, p.Slug, p.Name))
            .ToListAsync(ct);

        return new PagedResult<ProjectSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<ProjectResponse> UpdateAsync(
        Guid projectId,
        UpdateProjectRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var project = await _context.Projects
            .Include(p => p.Teams)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        project.Name = request.Name.Trim();
        project.Description = request.Description?.Trim() ?? project.Description;
        project.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return MapToResponse(project);
    }

    // -------------------------------------------------------------------------
    private static string NormalizeSlug(string input) =>
        System.Text.RegularExpressions.Regex
            .Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');

    private static ProjectResponse MapToResponse(Project p) =>
        new(p.Id, p.OrganizationId, p.Slug, p.Name, p.Description, p.IsActive,
            p.Teams.Count(t => t.IsActive), p.CreatedAt);

    private async Task<ProjectResponse> MapToResponseAsync(Project p, CancellationToken ct)
    {
        var teamCount = await _context.Teams.CountAsync(t => t.ProjectId == p.Id && t.IsActive, ct);
        return new(p.Id, p.OrganizationId, p.Slug, p.Name, p.Description, p.IsActive, teamCount, p.CreatedAt);
    }
}
