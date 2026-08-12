using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Scoped decorator that answers a repeated permission question once per request.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every action authorizes twice or more against the same scope: the controller guard runs
/// <see cref="HasRoleAsync"/>, then a service method re-resolves the same scope to decide what it is
/// allowed to touch. Each of those calls costs one query, and two when the direct role match fails
/// and org-admin inheritance has to be tested. Memoizing collapses them to the first one.
/// </para>
/// <para>
/// The cache lives exactly as long as the request. That is deliberate: it is short enough that role
/// changes made elsewhere are picked up on the caller's next request without any invalidation
/// machinery, which is what keeps this safe to add before the distributed cache exists. Anything
/// longer-lived needs the explicit invalidation described in the caching design, because a stale
/// grant outliving a revocation is a security bug rather than a cache miss.
/// </para>
/// <para>
/// A plain dictionary is the right structure here even though it is not thread-safe. This decorator
/// is scoped, so it shares its lifetime — and its threading contract — with the repository chain
/// beneath it, which is ultimately backed by a DbContext that already forbids concurrent use.
/// </para>
/// </remarks>
public sealed class MemoizingRbacService : IRbacService
{
    private readonly IRbacService _inner;
    private readonly Dictionary<RoleCheck, bool> _answered = [];

    public MemoizingRbacService(IRbacService inner) => _inner = inner;

    public async Task<bool> HasRoleAsync(
        Guid userId,
        RoleType minimumRole,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var key = new RoleCheck(userId, minimumRole, scope, scopeId);

        if (_answered.TryGetValue(key, out var cached))
            return cached;

        var answer = await _inner.HasRoleAsync(userId, minimumRole, scope, scopeId, ct);
        _answered[key] = answer;
        return answer;
    }

    // ── Writes ────────────────────────────────────────────────────────────────
    // Every mutation drops the whole memo rather than the keys it looks like it touched. Assigning
    // OrgAdmin at an organization changes the answer for every project and team underneath it, so
    // targeted eviction would have to re-derive the hierarchy — more work, and more ways to be
    // subtly wrong, than discarding a dictionary that only ever holds a handful of entries.

    public async Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
        CancellationToken ct = default)
    {
        var assignment = await _inner.AssignRoleAsync(userId, role, scope, scopeId, assignedBy, ct);
        _answered.Clear();
        return assignment;
    }

    public async Task RemoveRoleAsync(Guid userId, RoleType role, RoleScope scope, Guid scopeId, CancellationToken ct = default)
    {
        await _inner.RemoveRoleAsync(userId, role, scope, scopeId, ct);
        _answered.Clear();
    }

    public async Task RemoveAllRolesAsync(Guid userId, RoleScope scope, Guid scopeId, CancellationToken ct = default)
    {
        await _inner.RemoveAllRolesAsync(userId, scope, scopeId, ct);
        _answered.Clear();
    }

    // ── Pass-through reads ────────────────────────────────────────────────────
    // Not memoized: both return mutable entity instances rather than a yes/no, and callers that hold
    // one expect it to track the change tracker the way it does today.

    public Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
        => _inner.GetUserRolesAsync(userId, ct);

    public Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(RoleScope scope, Guid scopeId, CancellationToken ct = default)
        => _inner.GetScopeRolesAsync(scope, scopeId, ct);

    /// <summary>
    /// One permission question. <paramref name="MinimumRole"/> is part of the identity because
    /// "is this user at least a TeamMember here" and "is this user at least a Reader here" are
    /// different questions with different answers.
    /// </summary>
    private readonly record struct RoleCheck(
        Guid UserId,
        RoleType MinimumRole,
        RoleScope Scope,
        Guid ScopeId);
}
