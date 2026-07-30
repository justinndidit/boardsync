using BoardSync.Api.Modules.OrgProject.Domain.Helpers;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

public class ProjectService : IProjectService
{
    private readonly IProjectRepository _projectRepo;
    private readonly IOrganizationRepository _organizationRepo;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        IProjectRepository projectRepository,
        IOrganizationRepository organizationRepository,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<ProjectService> logger)
    {
        _projectRepo = projectRepository;
        _organizationRepo = organizationRepository;
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
        if (!await _organizationRepo.ExistsActiveAsync(orgId, ct))
            throw new NotFoundException("Organization", orgId);

        var slug = Slug.From(request.Slug ?? request.Name);

        if (await _projectRepo.SlugExistsInOrganizationAsync(orgId, slug, ct))
            throw new ConflictException($"A project with slug '{slug}' already exists in this organization.");

        var project = new Project
        {
            OrganizationId = orgId,
            Slug = slug,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _projectRepo.Add(project);
        await _projectRepo.SaveChangesAsync(ct);

        // Creator becomes ProjectAdmin
        await _rbac.AssignRoleAsync(createdBy, RoleType.ProjectAdmin, RoleScope.Project, project.Id, createdBy, ct);

        await _eventBus.PublishAsync(new ProjectCreated(project.Id, orgId, project.Name, project.Slug, createdBy), ct);

        _logger.LogInformation("Project '{Name}' ({Id}) created in org {OrgId} by {UserId}",
            project.Name, project.Id, orgId, createdBy);

        return await MapToResponseAsync(project, ct);
    }

    public async Task<ProjectResponse> GetByIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _projectRepo.GetActiveAsync(projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        return await MapToResponseAsync(project, ct);
    }

    public Task<bool> ExistsAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.ExistsActiveAsync(projectId, ct);

    public async Task<PagedResult<ProjectSummaryResponse>> GetForOrgAsync(
        Guid orgId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var (projects, total) = await _projectRepo.GetForOrganizationAsync(
            orgId, pagination.Skip, pagination.PageSize, ct);

        var items = projects
            .Select(p => new ProjectSummaryResponse(p.Id, p.Slug, p.Name))
            .ToList();

        return new PagedResult<ProjectSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<ProjectResponse> UpdateAsync(
        Guid projectId,
        UpdateProjectRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var project = await _projectRepo.GetActiveAsync(projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        project.Name = request.Name.Trim();
        project.Description = request.Description?.Trim() ?? project.Description;
        project.UpdatedAt = DateTime.UtcNow;

        await _projectRepo.SaveChangesAsync(ct);
        return await MapToResponseAsync(project, ct);
    }

    // -------------------------------------------------------------------------

    private async Task<ProjectResponse> MapToResponseAsync(Project p, CancellationToken ct)
    {
        var teamCount = await _projectRepo.GetActiveTeamCountAsync(p.Id, ct);
        return new(p.Id, p.OrganizationId, p.Slug, p.Name, p.Description, p.IsActive, teamCount, p.CreatedAt);
    }
}
