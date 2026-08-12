using BoardSync.Api.Modules.Activity.Models;

namespace BoardSync.Api.Modules.Activity.Repositories.Interfaces;

/// <summary>
/// Data access for the activity log — the <c>activity.ActivityLogs</c> table, plus the name
/// lookups the feed needs to render an entry.
/// </summary>
/// <remarks>
/// The log is append-only: there is no update or delete here by design. Entries record what
/// happened, and rewriting history is not a thing the product does.
/// </remarks>
public interface IActivityRepository
{
    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Appends one entry and persists it immediately.</summary>
    Task AddAsync(ActivityLog entry, CancellationToken ct = default);

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Total entries filed against the given organizations.</summary>
    Task<int> CountForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default);

    /// <summary>
    /// A page of entries, newest first, using an offset.
    /// </summary>
    /// <remarks>
    /// Kept for the documented <c>?page=</c> contract. Offsets make the database walk and discard
    /// every row before the page, so depth costs time — prefer
    /// <see cref="GetPageAfterAsync"/> for anything that scrolls.
    /// </remarks>
    Task<IReadOnlyList<ActivityLog>> GetPageAsync(
        IReadOnlyCollection<Guid> organizationIds,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// A page of entries strictly older than the given position, newest first — the keyset path.
    /// Seeks straight to the cursor instead of counting past everything before it.
    /// </summary>
    Task<IReadOnlyList<ActivityLog>> GetPageAfterAsync(
        IReadOnlyCollection<Guid> organizationIds,
        DateTime occurredBefore,
        Guid idBefore,
        int take,
        CancellationToken ct = default);

    // ── Name resolution ───────────────────────────────────────────────────────
    // Actor, organization, project and team names are looked up rather than snapshotted, so the
    // feed shows what things are called now. The subject's own title is snapshotted on the row
    // instead, so deleted entities still read correctly.

    /// <summary>Display names for the given users, keyed by id. Missing users are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>Names for the given organizations, keyed by id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetOrganizationNamesAsync(
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default);

    /// <summary>Names for the given projects, keyed by id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetProjectNamesAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken ct = default);

    /// <summary>Names for the given teams, keyed by id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>One user's display name, or "Unknown" if the user is gone.</summary>
    Task<string> GetUserNameAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One organization's name, or empty if it is gone.</summary>
    Task<string> GetOrganizationNameAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>One team's name, or empty if it is gone.</summary>
    Task<string> GetTeamNameAsync(Guid teamId, CancellationToken ct = default);

    // ── Subject lookups ───────────────────────────────────────────────────────
    // Domain events carry ids, not prose. These fill in the human-readable subject an entry needs
    // before it can be written, and all of them tolerate a subject that has since been deleted —
    // a missing name costs a thinner feed line, never a lost entry.

    /// <summary>
    /// The organization owning a project and that project's name, or null if the project is gone —
    /// in which case there is nothing to file the entry under and it is dropped.
    /// </summary>
    Task<ProjectScope?> GetProjectScopeAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>One work item's title, or empty if it is gone.</summary>
    Task<string> GetWorkItemTitleAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>A work item's title and owning project, or null if it is gone.</summary>
    Task<WorkItemSubject?> GetWorkItemSubjectAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>One comment's body, or null if it is gone.</summary>
    Task<string?> GetCommentBodyAsync(Guid commentId, CancellationToken ct = default);
}

/// <summary>The organization a project belongs to, with the project's current name.</summary>
public readonly record struct ProjectScope(Guid OrganizationId, string Name);

/// <summary>A work item's title and the project it lives in.</summary>
public readonly record struct WorkItemSubject(string Title, Guid ProjectId);
