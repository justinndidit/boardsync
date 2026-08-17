using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.Rbac.Repositories.Interfaces;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Watches for the events that change what somebody is allowed to see, and has their live
/// subscriptions re-checked.
/// </summary>
/// <remarks>
/// <para>
/// These are exactly the events that can take access away. Adding a member or granting a role
/// cannot make an existing subscription invalid, but they are handled too: the same re-check that
/// drops a revoked topic is harmless when nothing changed, and treating the whole set uniformly
/// removes the question of which direction a role change went.
/// </para>
/// <para>
/// <see cref="ProjectTeamAssigned"/> is the odd one out: it changes nobody's grants, it moves the
/// project to a different place in the tree. Everyone on the previous team silently loses access to
/// it and everyone on the new team gains it, so both teams' members have to be re-checked even
/// though no role assignment was touched.
/// </para>
/// <para>
/// Runs on the outbox dispatcher, so it inherits the outbox's guarantee — a role change that
/// committed will be acted on, even if the instance handling it dies first.
/// </para>
/// </remarks>
public class AccessChangeHandlers :
    IEventHandler<OrgMemberRoleChanged>,
    IEventHandler<MemberRemovedFromOrg>,
    IEventHandler<MemberAddedToOrg>,
    IEventHandler<ProjectRoleChanged>,
    IEventHandler<MemberRemovedFromTeam>,
    IEventHandler<MemberAddedToTeam>,
    IEventHandler<ProjectTeamAssigned>
{
    private readonly IAccessChangeNotifier _notifier;
    private readonly IRoleAssignmentRepository _repository;

    public AccessChangeHandlers(
        IAccessChangeNotifier notifier,
        IRoleAssignmentRepository repository)
    {
        _notifier = notifier;
        _repository = repository;
    }

    public Task HandleAsync(OrgMemberRoleChanged e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public Task HandleAsync(MemberRemovedFromOrg e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public Task HandleAsync(MemberAddedToOrg e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public Task HandleAsync(ProjectRoleChanged e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public Task HandleAsync(MemberRemovedFromTeam e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public Task HandleAsync(MemberAddedToTeam e, CancellationToken ct = default) =>
        _notifier.AnnounceAsync(e.UserId, ct);

    public async Task HandleAsync(ProjectTeamAssigned e, CancellationToken ct = default)
    {
        // Both teams, and de-duplicated: a project reassigned between two teams that share people
        // would otherwise audit those people twice for no benefit.
        var previous = await _repository.GetTeamMemberUserIdsAsync(e.PreviousTeamId, ct);
        var current = await _repository.GetTeamMemberUserIdsAsync(e.NewTeamId, ct);

        foreach (var userId in previous.Concat(current).Distinct())
            await _notifier.AnnounceAsync(userId, ct);
    }
}
