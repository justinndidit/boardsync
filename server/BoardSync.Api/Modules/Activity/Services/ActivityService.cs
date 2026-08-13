using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Modules.Activity.Repositories.Interfaces;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Activity.Services;

/// <inheritdoc />
public class ActivityRecorder : IActivityRecorder
{
    private readonly IActivityRepository _repository;
    private readonly ILogger<ActivityRecorder> _logger;

    public ActivityRecorder(IActivityRepository repository, ILogger<ActivityRecorder> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task RecordAsync(ActivityLog entry, CancellationToken ct = default)
    {
        var written = await _repository.AddIfNewAsync(entry, ct);

        if (written)
        {
            _logger.LogDebug("Recorded activity {Verb} on {EntityType} {EntityId} in org {OrgId}",
                entry.Verb, entry.EntityType, entry.EntityId, entry.OrganizationId);
        }
        else
        {
            // Expected whenever the outbox redelivers. Logged so a *storm* of them is visible —
            // that would mean messages are not being marked dispatched.
            _logger.LogDebug("Activity for event {EventId} already recorded; skipping duplicate.",
                entry.EventId);
        }
    }
}

/// <inheritdoc />
public class ActivityQueryService : IActivityQueryService
{
    private readonly IActivityRepository _repository;

    public ActivityQueryService(IActivityRepository repository)
    {
        _repository = repository;
    }

    public async Task<PagedResult<ActivityResponse>> GetForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0)
            return PagedResult<ActivityResponse>.Empty(pagination.Page, pagination.PageSize);

        var total = await _repository.CountForOrganizationsAsync(organizationIds, ct);

        // A cursor seeks straight to the client's last position; an offset makes Postgres walk and
        // discard every row before it, which is what makes deep pages progressively slower. An
        // unparseable cursor falls through to the offset path rather than failing the request.
        var rows = ActivityCursor.TryDecode(pagination.Cursor, out var cursor)
            ? await _repository.GetPageAfterAsync(
                organizationIds, cursor.OccurredAt, cursor.Id, pagination.PageSize, ct)
            : await _repository.GetPageAsync(
                organizationIds, pagination.Skip, pagination.PageSize, ct);

        var items = await ProjectAsync(rows, ct);

        // Only offered when the page came back full. A short page is the end of the feed, and
        // handing back a cursor there invites a client to poll a position that will never advance.
        var nextCursor = rows.Count == pagination.PageSize
            ? new ActivityCursor(rows[^1].OccurredAt, rows[^1].Id).Encode()
            : null;

        return new PagedResult<ActivityResponse>(
            items, total, pagination.Page, pagination.PageSize, nextCursor);
    }

    /// <summary>
    /// Resolves the names that are looked up rather than snapshotted — actor, organization,
    /// project and team all rename freely, so the feed shows what they are called now. The
    /// subject's own title is not among them; that one is snapshotted on the row so deleted
    /// entities still read correctly.
    /// </summary>
    private async Task<List<ActivityResponse>> ProjectAsync(
        IReadOnlyList<ActivityLog> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var actorMap = await _repository.GetUserNamesAsync(
            rows.Select(a => a.ActorId).Distinct().ToList(), ct);

        var orgMap = await _repository.GetOrganizationNamesAsync(
            rows.Select(a => a.OrganizationId).Distinct().ToList(), ct);

        var projectMap = await _repository.GetProjectNamesAsync(
            rows.Where(a => a.ProjectId.HasValue).Select(a => a.ProjectId!.Value).Distinct().ToList(), ct);

        var teamMap = await _repository.GetTeamNamesAsync(
            rows.Where(a => a.TeamId.HasValue).Select(a => a.TeamId!.Value).Distinct().ToList(), ct);

        return rows.Select(a => new ActivityResponse(
            a.Id,
            $"{a.EntityType}.{a.Verb}",
            a.EntityType,
            a.Verb,
            a.EntityId,
            a.EntityTitle,
            DetailFor(a),
            a.ActorId,
            actorMap.GetValueOrDefault(a.ActorId, "Unknown"),
            a.OrganizationId,
            orgMap.GetValueOrDefault(a.OrganizationId, string.Empty),
            a.ProjectId,
            a.ProjectId.HasValue ? projectMap.GetValueOrDefault(a.ProjectId.Value) : null,
            a.TeamId,
            a.TeamId.HasValue ? teamMap.GetValueOrDefault(a.TeamId.Value) : null,
            a.OccurredAt
        )).ToList();
    }

    /// <summary>
    /// Renders the one-line description shown under an entry. Blank strings are folded into null
    /// first, so a field going from empty to set reads "Description: rewrite" rather than
    /// "Description:  → rewrite".
    /// </summary>
    private static string? DetailFor(ActivityLog a)
    {
        var from = Blank(a.OldValue) ? null : a.OldValue;
        var to = Blank(a.NewValue) ? null : a.NewValue;

        // Membership and comment entries already say what happened in their Type, so the detail is
        // just the subject — "ada-1" reads better under Team.MemberAdded than "Member set to ada-1".
        if (a.Verb is ActivityVerb.MemberAdded or ActivityVerb.MemberRemoved or ActivityVerb.Commented)
            return to ?? from;

        if (a.FieldName is null)
            return to ?? from;

        return (from, to) switch
        {
            (null, null) => a.FieldName,
            (null, var set) => $"{a.FieldName}: {set}",
            (var cleared, null) => $"{a.FieldName}: {cleared} removed",
            var (before, after) => $"{a.FieldName}: {before} → {after}"
        };
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
