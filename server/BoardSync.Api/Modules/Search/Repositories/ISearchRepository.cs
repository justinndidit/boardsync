using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Rbac.Models;

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
/// <b>Every method is scoped by what the caller may read, which is not the same as which
/// organizations they belong to.</b> This interface used to take a list of the caller's organization
/// ids and read everything inside them, which quietly made organization membership equivalent to
/// access to every project in the organization — the one thing
/// <see cref="RolePermissions"/> says it is not. An org member on no team, holding no project role,
/// could read the title of every work item in the organization through search while
/// <c>GET /api/projects/{id}</c> correctly answered 404 for the same project.
/// </para>
/// <para>
/// So the project-scoped methods take a <see cref="ProjectVisibility"/>, and the organization-scoped
/// ones take the organizations where the caller actually holds <c>org:read</c>. Search must never be
/// a way to discover the existence of something you cannot otherwise see.
/// </para>
/// </remarks>
public interface ISearchRepository
{
    /// <summary>Active organizations matching the term by name or slug.</summary>
    Task<IReadOnlyList<SearchHit>> SearchOrganizationsAsync(
        Guid[] organizationIds, string term, int take, CancellationToken ct = default);

    /// <summary>Active members of those organizations matching by display name or email.</summary>
    Task<IReadOnlyList<SearchHit>> SearchMembersAsync(
        Guid[] organizationIds, string term, int take, CancellationToken ct = default);

    /// <summary>Active projects the caller may read, matching the term by name or slug.</summary>
    Task<IReadOnlyList<SearchHit>> SearchProjectsAsync(
        ProjectVisibility visibility, string term, int take, CancellationToken ct = default);

    /// <summary>Active work items in projects the caller may read, matching by title.</summary>
    Task<IReadOnlyList<SearchHit>> SearchWorkItemsAsync(
        ProjectVisibility visibility, string term, int take, CancellationToken ct = default);
}
