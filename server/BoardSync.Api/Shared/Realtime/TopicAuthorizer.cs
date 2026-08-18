using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Decides whether a user may listen on a topic.
/// </summary>
/// <remarks>
/// A subscription is a standing grant to receive everything on a channel, so it has to be
/// authorized at least as carefully as the REST endpoint that would return the same data. Getting
/// this wrong does not throw — it quietly streams another organization's work to someone who should
/// never see it.
/// </remarks>
public interface ITopicAuthorizer
{
    /// <summary>Whether this user may subscribe to this topic.</summary>
    Task<bool> CanSubscribeAsync(Guid userId, string topic, CancellationToken ct = default);
}

/// <inheritdoc />
public class TopicAuthorizer : ITopicAuthorizer
{
    private readonly IRbacService _rbac;
    private readonly ISprintRepository _sprints;
    private readonly ILogger<TopicAuthorizer> _logger;

    public TopicAuthorizer(
        IRbacService rbac,
        ISprintRepository sprints,
        ILogger<TopicAuthorizer> logger)
    {
        _rbac = rbac;
        _sprints = sprints;
        _logger = logger;
    }

    public async Task<bool> CanSubscribeAsync(Guid userId, string topic, CancellationToken ct = default)
    {
        if (!Topic.TryParse(topic, out var kind, out var id))
        {
            _logger.LogDebug("Rejected malformed topic '{Topic}' from user {UserId}", topic, userId);
            return false;
        }

        return kind switch
        {
            // A user's private channel is theirs alone. No role grants access to someone else's —
            // this is the one topic where being an OrgAdmin is not a reason to listen.
            TopicKind.User => id == userId,

            // Reading is the floor for everything else: if you can read it over HTTP you can watch
            // it over the socket, and if you cannot, you cannot. Each topic asks for the same
            // permission the equivalent GET endpoint asks for.
            TopicKind.Organization =>
                await _rbac.HasPermissionAsync(userId, Permissions.OrgRead, RoleScope.Organization, id, ct),

            TopicKind.Project =>
                await _rbac.HasPermissionAsync(userId, Permissions.ProjectRead, RoleScope.Project, id, ct),

            TopicKind.Team =>
                await _rbac.HasPermissionAsync(userId, Permissions.TeamRead, RoleScope.Team, id, ct),

            // Sprints carry no scope of their own — they hang off a team — so the sprint's team is
            // resolved first and the team's role decides. A sprint that no longer exists denies.
            TopicKind.Sprint => await CanSubscribeToSprintAsync(userId, id, ct),

            _ => false
        };
    }

  private async Task<bool> CanSubscribeToSprintAsync(Guid userId, Guid sprintId, CancellationToken ct)
  {
    var sprint = await _sprints.GetByIdAsync(sprintId, ct);

    if (sprint is null) return false;

    return await _rbac.HasPermissionAsync(
        userId,
        Permissions.ProjectRead,  // ← matches ProjectId scope
        RoleScope.Project,        // ← changed from Team to Project
        sprint.ProjectId,         // ← your original, correct
        ct);
   }
}