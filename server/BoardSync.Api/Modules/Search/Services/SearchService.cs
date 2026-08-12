using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
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

    public SearchService(ISearchRepository repository)
    {
        _repository = repository;
    }

    public async Task<GlobalSearchResponse> SearchAsync(
        Guid userId,
        string term,
        CancellationToken ct = default)
    {
        var normalized = term.Trim().ToLowerInvariant();

        var orgIds = await _repository.GetOrganizationIdsForUserAsync(userId, ct);

        if (orgIds.Count == 0)
            return new GlobalSearchResponse([], [], [], []);

        // These four run sequentially by design. They share the one DbContext scoped to this
        // request, which is not thread-safe: starting them concurrently and awaiting with
        // Task.WhenAll throws "a second operation was started on this context instance".
        var organizations = await _repository.SearchOrganizationsAsync(
            orgIds, normalized, ISearchService.ResultsPerCategory, ct);

        var projects = await _repository.SearchProjectsAsync(
            orgIds, normalized, ISearchService.ResultsPerCategory, ct);

        var members = await _repository.SearchMembersAsync(
            orgIds, normalized, ISearchService.ResultsPerCategory, ct);

        var workItems = await _repository.SearchWorkItemsAsync(
            orgIds, normalized, ISearchService.ResultsPerCategory, ct);

        return new GlobalSearchResponse(
            Organizations: organizations,
            Projects: projects,
            Members: members,
            WorkItems: workItems);
    }
}
