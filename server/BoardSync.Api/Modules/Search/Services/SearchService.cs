using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Search.Repositories;

namespace BoardSync.Api.Modules.Search.Services;

/// <summary>
/// Global search across organizations, projects, members and work items.
/// </summary>
public interface ISearchService
{
    /// <summary>Minimum term length. Shorter terms match nearly everything and cost a scan to say so.</summary>
    const int MinimumTermLength = 2;

    /// <summary>Results per category.</summary>
    const int ResultsPerCategory = 10;

    /// <summary>
    /// Searches everything the user can see. The term is normalized here, so callers pass whatever
    /// was typed.
    /// </summary>
    Task<GlobalSearchResponse> SearchAsync(Guid userId, string term, CancellationToken ct = default);
}

/// <inheritdoc />
public class SearchService : ISearchService
{
    private readonly ISearchRepository _repository;
    private readonly IRbacService _rbac;

    public SearchService(ISearchRepository repository, IRbacService rbac)
    {
        _repository = repository;
        _rbac = rbac;
    }

    public async Task<GlobalSearchResponse> SearchAsync(
        Guid userId,
        string term,
        CancellationToken ct = default)
    {
        var normalized = term.Trim().ToLowerInvariant();

        // Each category is scoped by the permission its own endpoint would demand, so a hit here can
        // always be opened. Searching is not a weaker gate than reading; it is the same gate asked
        // without a scope.
        //
        // Both of these resolve from one cached access snapshot, so the pair costs no more than a
        // single permission check would.
        var readableOrgs = await _rbac.GetVisibleOrganizationIdsAsync(userId, Permissions.OrgRead, ct);
        var readableProjects = await _rbac.GetProjectVisibilityAsync(userId, Permissions.ProjectRead, ct);
        var readableWorkItems = await _rbac.GetProjectVisibilityAsync(userId, Permissions.WorkItemRead, ct);

        if (readableOrgs.Length == 0 && readableProjects.IsEmpty && readableWorkItems.IsEmpty)
            return new GlobalSearchResponse([], [], [], []);

        // These four run sequentially by design. They share the one DbContext scoped to this
        // request, which is not thread-safe: starting them concurrently and awaiting with
        // Task.WhenAll throws "a second operation was started on this context instance".
        var organizations = await _repository.SearchOrganizationsAsync(
            readableOrgs, normalized, ISearchService.ResultsPerCategory, ct);

        var projects = await _repository.SearchProjectsAsync(
            readableProjects, normalized, ISearchService.ResultsPerCategory, ct);

        // Scoped to organizations rather than projects on purpose: an organization's member list is
        // readable by anyone holding org:read, and this is the same list narrowed by a term.
        var members = await _repository.SearchMembersAsync(
            readableOrgs, normalized, ISearchService.ResultsPerCategory, ct);

        // workitem:read rather than project:read. They travel together in every role that has either,
        // but the permission a work item result is opened with is the one that should gate finding it.
        var workItems = await _repository.SearchWorkItemsAsync(
            readableWorkItems, normalized, ISearchService.ResultsPerCategory, ct);

        return new GlobalSearchResponse(
            Organizations: organizations,
            Projects: projects,
            Members: members,
            WorkItems: workItems);
    }
}
