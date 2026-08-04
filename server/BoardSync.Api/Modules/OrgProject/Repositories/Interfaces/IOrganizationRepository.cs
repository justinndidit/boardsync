using BoardSync.Api.Modules.OrgProject.Domain.Models;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Persistence for the Organization aggregate and its memberships.
///
/// Pure unit of work: <c>Add</c>/<c>Remove</c> stage changes in memory and nothing is written
/// until <see cref="SaveChangesAsync"/> is called, so a service can compose several writes into
/// one transaction.
/// </summary>
public interface IOrganizationRepository
{
    // ── Organizations ─────────────────────────────────────────────────────────

    /// <summary>Active organization by ID, tracked for mutation, or null.</summary>
    Task<Organization?> GetActiveAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Active organization by unique slug, tracked for mutation, or null.</summary>
    Task<Organization?> GetActiveBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Whether an active organization exists, without loading it.</summary>
    Task<bool> ExistsActiveAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Whether the slug is taken. Checks inactive organizations too, because the unique index
    /// spans them and a soft-deleted organization still owns its slug.
    /// </summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    /// <summary>Member and active-project counts for one organization, in a single round trip.</summary>
    Task<OrganizationCounts> GetCountsAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Active organizations the user is a member of, ordered by name, with counts.</summary>
    Task<(IReadOnlyList<OrganizationSummaryRecord> Items, int TotalCount)> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct = default);

    void Add(Organization organization);

    // ── Memberships ───────────────────────────────────────────────────────────

    Task<bool> IsMemberAsync(Guid organizationId, Guid userId, CancellationToken ct = default);

    Task<OrganizationMembership?> GetMembershipAsync(Guid organizationId, Guid userId, CancellationToken ct = default);

    /// <summary>Members of an organization ordered by join date, joined to their user profiles.</summary>
    Task<(IReadOnlyList<MemberRecord> Items, int TotalCount)> GetMembersAsync(
        Guid organizationId, int skip, int take, CancellationToken ct = default);

    void AddMembership(OrganizationMembership membership);

    void RemoveMembership(OrganizationMembership membership);

    // ── Unit of work ──────────────────────────────────────────────────────────

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a single database transaction, retried as a unit.
    ///
    /// Needed because creating an organization also writes a role assignment owned by the RBAC
    /// module, which saves through its own service — so the work cannot be folded into one
    /// <see cref="SaveChangesAsync"/>. Every repository and service in a request shares the same
    /// scoped <c>DbContext</c>, so their writes enlist in this transaction automatically.
    /// Wrapping in the execution strategy is mandatory here: <c>EnableRetryOnFailure</c> refuses
    /// to retry a user-initiated transaction otherwise.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);
}
