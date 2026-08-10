using BoardSync.Api.Data;
using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Shared.Kernel;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Activity.Services;

/// <inheritdoc />
public class ActivityRecorder : IActivityRecorder
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<ActivityRecorder> _logger;

    public ActivityRecorder(BoardSyncDbContext context, ILogger<ActivityRecorder> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task RecordAsync(ActivityLog entry, CancellationToken ct = default)
    {
        _context.ActivityLogs.Add(entry);
        await _context.SaveChangesAsync(ct);

        _logger.LogDebug("Recorded activity {Verb} on {EntityType} {EntityId} in org {OrgId}",
            entry.Verb, entry.EntityType, entry.EntityId, entry.OrganizationId);
    }
}

/// <inheritdoc />
public class ActivityQueryService : IActivityQueryService
{
    private readonly BoardSyncDbContext _context;

    public ActivityQueryService(BoardSyncDbContext context)
    {
        _context = context;
    }

    public async Task<PagedResult<ActivityResponse>> GetForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0)
            return PagedResult<ActivityResponse>.Empty(pagination.Page, pagination.PageSize);

        var orgIds = organizationIds as List<Guid> ?? organizationIds.ToList();

        var query = _context.ActivityLogs.Where(a => orgIds.Contains(a.OrganizationId));

        var total = await query.CountAsync(ct);

        // Id is a tiebreaker, not decoration: entries written in the same transaction share an
        // OccurredAt to the microsecond, and without a total order the same row can appear on two
        // pages while another is skipped.
        var rows = await query
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

        var items = await ProjectAsync(rows, ct);

        return new PagedResult<ActivityResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    /// <summary>
    /// Resolves the names that are looked up rather than snapshotted — actor, organization,
    /// project and team all rename freely, so the feed shows what they are called now. The
    /// subject's own title is not among them; that one is snapshotted on the row so deleted
    /// entities still read correctly.
    /// </summary>
    private async Task<List<ActivityResponse>> ProjectAsync(List<ActivityLog> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var actorIds = rows.Select(a => a.ActorId).Distinct().ToList();
        var actorMap = await _context.Users
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var orgIds = rows.Select(a => a.OrganizationId).Distinct().ToList();
        var orgMap = await _context.Organizations
            .Where(o => orgIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);

        var projectIds = rows.Where(a => a.ProjectId.HasValue).Select(a => a.ProjectId!.Value).Distinct().ToList();
        var projectMap = projectIds.Count == 0
            ? []
            : await _context.Projects
                .Where(p => projectIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var teamIds = rows.Where(a => a.TeamId.HasValue).Select(a => a.TeamId!.Value).Distinct().ToList();
        var teamMap = teamIds.Count == 0
            ? []
            : await _context.Teams
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

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
