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
    private readonly ITeamRepository _teamRepo;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        IProjectRepository projectRepository,
        IOrganizationRepository organizationRepository,
        ITeamRepository teamRepository,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<ProjectService> logger)
    {
        _projectRepo = projectRepository;
        _organizationRepo = organizationRepository;
        _teamRepo = teamRepository;
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

        // The assigned team is a required, restricting FK. Validating it here turns a would-be
        // foreign-key violation (500) into a 404, and stops a project in one organization from
        // being pointed at another organization's team.
        if (!await _teamRepo.ExistsActiveInOrgAsync(orgId, request.AssignedTeamId, ct))
            throw new NotFoundException(
                $"Active team '{request.AssignedTeamId}' was not found in organization '{orgId}'.");

        var slug = Slug.From(request.Slug ?? request.Name);

        if (await _projectRepo.SlugExistsInOrganizationAsync(orgId, slug, ct))
            throw new ConflictException($"A project with slug '{slug}' already exists in this organization.");

        var project = new Project
        {
            OrganizationId = orgId,
            AssignedTeamId = request.AssignedTeamId,
            Slug = slug,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _projectRepo.Add(project);
        _eventBus.Enqueue(new ProjectCreated(project.Id, orgId, project.Name, project.Slug, createdBy));
        await _projectRepo.SaveChangesAsync(ct);

        // Creator becomes ProjectAdmin
        await _rbac.AssignRoleAsync(createdBy, RoleType.ProjectAdmin, RoleScope.Project, project.Id, createdBy, ct);

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

    public Task<bool> AllowsSelfCertificationAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.AllowsSelfCertificationAsync(projectId, ct);

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

        // Captured before the assignments below overwrite them — the activity feed reports what
        // each field went from, not just that the project was touched.
        var changes = new List<(string Field, string? Old, string? New)>();
        var newName = request.Name.Trim();
        var newDescription = request.Description?.Trim() ?? project.Description;

        // Left alone when the client does not mention it: turning the QA separation off is a
        // deliberate act, not something a rename should be able to do by omission.
        var newSelfCertification = request.AllowSelfCertification ?? project.AllowSelfCertification;

        if (project.Name != newName)
            changes.Add(("Name", project.Name, newName));
        if (project.Description != newDescription)
            changes.Add(("Description", project.Description, newDescription));
        if (project.AllowSelfCertification != newSelfCertification)
            changes.Add(("AllowSelfCertification",
                project.AllowSelfCertification.ToString(), newSelfCertification.ToString()));

        project.Name = newName;
        project.Description = newDescription;
        project.AllowSelfCertification = newSelfCertification;
        project.UpdatedAt = DateTime.UtcNow;

        foreach (var (field, oldValue, newValue) in changes)
        {
            _eventBus.Enqueue(new ProjectUpdated(
                project.Id, project.OrganizationId, project.Name, field, oldValue, newValue, updatedBy));
        }

        await _projectRepo.SaveChangesAsync(ct);

        return await MapToResponseAsync(project, ct);
    }

    public async Task<ProjectResponse> AssignTeamAsync(
        Guid projectId,
        Guid teamId,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var project = await _projectRepo.GetActiveAsync(projectId, ct)
            ?? throw new NotFoundException(nameof(Project), projectId);

        if (!await _teamRepo.ExistsActiveInOrgAsync(project.OrganizationId, teamId, ct))
            throw new NotFoundException(
                $"Active team '{teamId}' was not found in organization '{project.OrganizationId}'.");

        var previousTeamId = project.AssignedTeamId;

        project.AssignedTeamId = teamId;
        project.UpdatedAt = DateTime.UtcNow;

        _eventBus.Enqueue(new ProjectTeamAssigned(
            project.Id, project.OrganizationId, project.Name, previousTeamId, teamId, updatedBy));

        await _projectRepo.SaveChangesAsync(ct);

        _logger.LogInformation("Project {ProjectId} reassigned to team {TeamId} by {UserId}",
            projectId, teamId, updatedBy);

        return await MapToResponseAsync(project, ct);
    }

    // -------------------------------------------------------------------------

    private async Task<ProjectResponse> MapToResponseAsync(Project p, CancellationToken ct)
    {
        var team = await _teamRepo.GetActiveByIdAsync(p.AssignedTeamId, ct);

        return new(p.Id, p.OrganizationId, p.Slug, p.Name, p.Description, p.IsActive,
            p.AssignedTeamId, team?.Name ?? string.Empty, p.AllowSelfCertification, p.CreatedAt);
    }
}
