using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Advances the access cache generation whenever a user's grants change.
/// </summary>
/// <remarks>
/// <para>
/// The read side is cached one layer down, in <see cref="CachingAccessResolver"/> — a user's grants
/// are cached once and every question is answered from them in memory. This decorator exists purely
/// so that writes reach that cache: it wraps each mutation and bumps the user's generation
/// afterwards, which orphans everything cached about them.
/// </para>
/// <para>
/// Ordering matters. The generation is advanced <em>after</em> the write, so a concurrent reader
/// that repopulates the cache mid-flight is orphaned by the bump rather than surviving it.
/// </para>
/// <para>
/// The per-request memo is dropped as well. It sits above the distributed cache and would otherwise
/// keep answering from grants this very request has just changed.
/// </para>
/// </remarks>
public sealed class InvalidatingRbacService : IRbacService
{
    private readonly IRbacService _inner;
    private readonly IAccessCacheVersion _version;
    private readonly IAccessMemo _memo;

    public InvalidatingRbacService(
        IRbacService inner,
        IAccessCacheVersion version,
        IAccessMemo memo)
    {
        _inner = inner;
        _version = version;
        _memo = memo;
    }

    // ── Reads ─────────────────────────────────────────────────────────────────
    // Pass-through. Caching happens beneath this, on the grants themselves.

    public Task<bool> HasRoleAsync(
        Guid userId,
        RoleType minimumRole,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
        => _inner.HasRoleAsync(userId, minimumRole, scope, scopeId, ct);

    public Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
        => _inner.GetUserRolesAsync(userId, ct);

    public Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
        => _inner.GetScopeRolesAsync(scope, scopeId, ct);

    // ── Writes ────────────────────────────────────────────────────────────────

    public async Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
        CancellationToken ct = default)
    {
        var assignment = await _inner.AssignRoleAsync(userId, role, scope, scopeId, assignedBy, ct);
        await InvalidateAsync(userId);
        return assignment;
    }

    public async Task RemoveRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        await _inner.RemoveRoleAsync(userId, role, scope, scopeId, ct);
        await InvalidateAsync(userId);
    }

    public async Task RemoveAllRolesAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        await _inner.RemoveAllRolesAsync(userId, scope, scopeId, ct);
        await InvalidateAsync(userId);
    }

    public async Task RemoveAllRolesInOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default)
    {
        await _inner.RemoveAllRolesInOrganizationAsync(userId, organizationId, ct);
        await InvalidateAsync(userId);
    }

    private async Task InvalidateAsync(Guid userId)
    {
        _memo.Clear();
        await _version.BumpAsync(userId);
    }
}
