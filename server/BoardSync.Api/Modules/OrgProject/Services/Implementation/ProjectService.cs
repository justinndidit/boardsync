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

        // An explicit key is honoured and a collision is an error the caller can act on; a derived
        // one silently disambiguates, because nobody chose it and failing on it would be a strange
        // thing to refuse a project over.
        var takenKeys = await _projectRepo.GetKeysInOrganizationAsync(orgId, ct);
        string key;

        if (request.Key is { Length: > 0 } requested)
        {
            key = requested.ToUpperInvariant();

            if (takenKeys.Contains(key))
                throw new ConflictException(
                    $"A project with key '{key}' already exists in this organization.");
        }
        else
        {
            key = ProjectKey.Unique(request.Name, takenKeys);
        }

        var project = new Project
        {
            OrganizationId = orgId,
            AssignedTeamId = request.AssignedTeamId,
            Slug = slug,
            Key = key,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _projectRepo.Add(project);
        _eventBus.Enqueue(new ProjectCreated(project.Id, orgId, project.Name, project.Slug, createdBy));
        await _projectRepo.SaveChangesAsync(ct);

        // Creator becomes ProjectAdmin
        await _rbac.AssignRoleAsync(createdBy, RoleType.ProjectAdmin, RoleScope.Project, project.Id, createdBy, ct: ct);

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

    public Task<int> TakeNextWorkItemNumberAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.TakeNextWorkItemNumberAsync(projectId, ct);

    public Task<string> GetKeyAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.GetKeyAsync(projectId, ct);

    public Task<Guid?> GetOrganizationIdAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.GetOrganizationIdAsync(projectId, ct);

    public Task<bool> AllowsSelfCertificationAsync(Guid projectId, CancellationToken ct = default) =>
        _projectRepo.AllowsSelfCertificationAsync(projectId, ct);

    /// <summary>
    /// An organization's projects, in the same shape as a single one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The full response, not a three-field summary.</b> This is what the projects page renders
    /// cards from, and a card shows the owning team and the creation date — so a summary of
    /// <c>(id, slug, name)</c> left the team blank and put <c>new Date(undefined)</c> on screen as
    /// "Invalid Date". The client's type had always declared the full shape; only the payload was
    /// short, which is why nothing failed to compile.
    /// </para>
    /// <para>
    /// Team names are resolved once per distinct team rather than once per project. A page of
    /// twenty projects in an organization with three teams is three lookups, not twenty.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<ProjectResponse>> GetForOrgAsync(
        Guid orgId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var (projects, total) = await _projectRepo.GetForOrganizationAsync(
            orgId, pagination.Skip, pagination.PageSize, ct);

        var teamNames = new Dictionary<Guid, string>();

        foreach (var teamId in projects.Select(p => p.AssignedTeamId).Distinct())
        {
            var team = await _teamRepo.GetActiveByIdAsync(teamId, ct);

            teamNames[teamId] = team?.Name ?? string.Empty;
        }

        var items = projects
            .Select(p => new ProjectResponse(
                p.Id, p.OrganizationId, p.Slug, p.Key, p.Name, p.Description, p.IsActive,
                p.AssignedTeamId, teamNames.GetValueOrDefault(p.AssignedTeamId, string.Empty),
                p.AllowSelfCertification, p.CreatedAt))
            .ToList();

        return new PagedResult<ProjectResponse>(items, total, pagination.Page, pagination.PageSize);
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

        return new(p.Id, p.OrganizationId, p.Slug, p.Key, p.Name, p.Description, p.IsActive,
            p.AssignedTeamId, team?.Name ?? string.Empty, p.AllowSelfCertification, p.CreatedAt);
    }
}
