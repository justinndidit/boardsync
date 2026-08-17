using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Scoped decorator that loads a user's grants at most once per request.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every action authorizes more than once against the same scope: the controller guard runs,
/// then a service method re-resolves the same scope to decide what it is allowed to touch. Without
/// this each of those would deserialize the snapshot out of the cache again.
/// </para>
/// <para>
/// The memo lives exactly as long as the request, so a grant changed elsewhere is picked up on the
/// caller's next request with no invalidation machinery. Changes made <em>by this request</em> are a
/// different matter: a memo held across a write would answer with grants that no longer exist, so
/// every write path drops it through <see cref="IAccessMemo.Clear"/>.
/// </para>
/// <para>
/// Plain dictionaries are the right structure even though they are not thread-safe. This decorator
/// is scoped, so it shares its lifetime — and its threading contract — with the repository chain
/// beneath it, which is ultimately backed by a DbContext that already forbids concurrent use.
/// </para>
/// </remarks>
public sealed class MemoizingAccessResolver : IAccessResolver, IAccessMemo
{
    private readonly IAccessResolver _inner;
    private readonly Dictionary<Guid, AccessSnapshot> _snapshots = [];
    private readonly Dictionary<Guid, ProjectLocation?> _projectLocations = [];
    private readonly Dictionary<Guid, Guid?> _teamOrganizations = [];

    public MemoizingAccessResolver(IAccessResolver inner) => _inner = inner;

    /// <summary>
    /// Drops everything remembered so far.
    /// </summary>
    /// <remarks>
    /// Everything, rather than the entries a write looks like it touched. Granting OrgAdmin at an
    /// organization changes the answer for every team and project underneath it, and reassigning a
    /// project moves it under a different team entirely — targeted eviction would have to re-derive
    /// the tree, which is more work and more ways to be subtly wrong than discarding three
    /// dictionaries that only ever hold a handful of entries.
    /// </remarks>
    public void Clear()
    {
        _snapshots.Clear();
        _projectLocations.Clear();
        _teamOrganizations.Clear();
    }

    public async Task<AccessSnapshot> GetSnapshotAsync(Guid userId, CancellationToken ct = default)
    {
        if (_snapshots.TryGetValue(userId, out var cached))
            return cached;

        var snapshot = await _inner.GetSnapshotAsync(userId, ct);
        _snapshots[userId] = snapshot;
        return snapshot;
    }

    public async Task<ProjectLocation?> GetProjectLocationAsync(Guid projectId, CancellationToken ct = default)
    {
        if (_projectLocations.TryGetValue(projectId, out var cached))
            return cached;

        var location = await _inner.GetProjectLocationAsync(projectId, ct);
        _projectLocations[projectId] = location;
        return location;
    }

    public async Task<Guid?> GetTeamOrganizationIdAsync(Guid teamId, CancellationToken ct = default)
    {
        if (_teamOrganizations.TryGetValue(teamId, out var cached))
            return cached;

        var organizationId = await _inner.GetTeamOrganizationIdAsync(teamId, ct);
        _teamOrganizations[teamId] = organizationId;
        return organizationId;
    }
}
