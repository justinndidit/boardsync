using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// What the workspace dashboard is allowed to count, resolved once from the caller's grants.
/// </summary>
/// <remarks>
/// <para>
/// Three sets rather than one, because the four counters are gated on three different permissions
/// and it would be a coincidence, not a rule, if they always agreed. They do agree today — every
/// role carrying <c>project:read</c> also carries <c>workitem:read</c> — and a dashboard that
/// silently depends on that coincidence is one role definition away from over-reporting.
/// </para>
/// </remarks>
/// <param name="Organizations">Organizations the caller may read.</param>
/// <param name="Projects">Projects the caller may read.</param>
/// <param name="WorkItems">Projects whose work items the caller may read.</param>
public readonly record struct WorkspaceScope(
    Guid[] Organizations,
    ProjectVisibility Projects,
    ProjectVisibility WorkItems)
{
    /// <summary>Nothing visible anywhere — the counters are all zero without asking the database.</summary>
    public bool IsEmpty => Organizations.Length == 0 && Projects.IsEmpty && WorkItems.IsEmpty;
}

/// <summary>
/// Cross-organization reads for the workspace dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the per-aggregate repositories because these questions do not belong to one
/// organization, project or team — they span everything the caller can see.
/// </para>
/// <para>
/// "Everything the caller can see" used to mean "every organization they are a member of, and
/// everything inside it", which counted work items in projects the same caller would get a 404 from
/// if they opened one. The counters are scoped by permission now, so the dashboard reports the size
/// of the workspace the user actually has.
/// </para>
/// </remarks>
public interface IWorkspaceRepository
{
    /// <summary>
    /// The four dashboard counters, resolved in one round trip.
    /// </summary>
    /// <remarks>
    /// Composed as subqueries rather than four sequential queries, and the organization and project
    /// id sets stay in the database instead of being pulled into memory and shipped back as IN
    /// lists that grow with the user's membership.
    /// </remarks>
    Task<WorkspaceSummaryResponse> GetSummaryAsync(
        Guid userId,
        WorkspaceScope scope,
        CancellationToken ct = default);
}
