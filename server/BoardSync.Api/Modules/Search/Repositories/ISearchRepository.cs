using BoardSync.Api.Modules.OrgProject.Domain.DTOs;

namespace BoardSync.Api.Modules.Search.Repositories;

/// <summary>
/// Data access for global search.
/// </summary>
/// <remarks>
/// <para>
/// Search is the one read that deliberately crosses every module: it answers "where is this thing"
/// without the caller knowing what kind of thing it is. Rather than pretend otherwise, it owns its
/// own repository and queries the tables it needs directly. Fanning it out to each module's
/// repository would mean four interfaces each growing a <c>SearchBy…</c> method that only this
/// feature ever calls.
/// </para>
/// <para>
/// Every method is scoped by the organizations the caller belongs to — search must never be a way
/// to discover the existence of things you cannot otherwise see.
/// </para>
/// </remarks>
public interface ISearchRepository
{
    /// <summary>Organizations the user is a member of. Everything else is scoped to these.</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Active organizations matching the term by name or slug.</summary>
    Task<IReadOnlyList<SearchHit>> SearchOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds, string term, int take, CancellationToken ct = default);

    /// <summary>Active projects matching the term by name or slug.</summary>
    Task<IReadOnlyList<SearchHit>> SearchProjectsAsync(
        IReadOnlyCollection<Guid> organizationIds, string term, int take, CancellationToken ct = default);

    /// <summary>Active members of those organizations matching by display name or email.</summary>
    Task<IReadOnlyList<SearchHit>> SearchMembersAsync(
        IReadOnlyCollection<Guid> organizationIds, string term, int take, CancellationToken ct = default);

    /// <summary>Active work items in those organizations' projects matching by title.</summary>
    Task<IReadOnlyList<SearchHit>> SearchWorkItemsAsync(
        IReadOnlyCollection<Guid> organizationIds, string term, int take, CancellationToken ct = default);
}
