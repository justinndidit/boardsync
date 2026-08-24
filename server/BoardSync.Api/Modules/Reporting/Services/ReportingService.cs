using BoardSync.Api.Data;
using BoardSync.Api.Modules.Reporting.DTOs;
using BoardSync.Api.Modules.Reporting.Domain;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Reporting.Services;

/// <summary>Computed delivery metrics.</summary>
public interface IReportingService
{
    Task<SprintReport> GetSprintReportAsync(Guid sprintId, CancellationToken ct = default);

    Task<VelocityReport> GetVelocityAsync(
        Guid projectId, int sprintCount, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Everything here is computed from recorded facts.</b> That is the whole reason this module is
/// called Reporting and sits apart from the Intelligence module that will narrate over it: a model
/// asked to both compute and narrate produces plausible numbers, and nobody downstream can tell
/// which were computed and which were invented. Keeping the boundary in the filesystem makes it
/// harder to blur later than a comment would.
/// </para>
/// <para>
/// These figures mean more in BoardSync than in a tracker people update by hand, because the board
/// moves itself from git. "Reached In Review" is a pull request opening; "reached Awaiting QA" is a
/// merge. Cycle time measured off those is measuring the work, not measuring how diligently people
/// dragged cards.
/// </para>
/// </remarks>
public class ReportingService : IReportingService
{
    private readonly BoardSyncDbContext _context;

    public ReportingService(BoardSyncDbContext context)
    {
        _context = context;
    }

    /// <summary>Most sprints a velocity series will return.</summary>
    /// <remarks>
    /// A forecast built on two years of history is a forecast about a team that no longer exists.
    /// The cap is generous; the default is smaller.
    /// </remarks>
    public const int MaxVelocitySprints = 24;

    public async Task<SprintReport> GetSprintReportAsync(Guid sprintId, CancellationToken ct = default)
    {
        var sprint = await _context.Sprints.FirstOrDefaultAsync(s => s.Id == sprintId, ct)
            ?? throw new NotFoundException("Sprint", sprintId);

        var items = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems.Where(w => w.IsActive), sw => sw.WorkItemId, w => w.Id,
                (sw, w) => new { w.Id, w.State, w.StoryPoints })
            .ToListAsync(ct);

        var timelines = await LoadTimelinesAsync(items.Select(i => i.Id), ct);

        var committedPoints = items.Sum(i => i.StoryPoints ?? 0);
        var closed = items.Where(i => i.State == WorkItemState.Closed).ToList();

        var summary = new SprintSummary(
            SprintId: sprint.Id,
            Number: sprint.Number,
            Goal: sprint.Goal,
            StartDate: sprint.StartDate,
            EndDate: sprint.EndDate,
            Status: sprint.Status.ToString(),
            CommittedPoints: committedPoints,
            CompletedPoints: closed.Sum(i => i.StoryPoints ?? 0),
            CommittedItems: items.Count,
            CompletedItems: closed.Count,

            // Its own number because a sprint that looks behind may be finished work nobody has
            // verified — which is a different problem with a different owner.
            AwaitingVerificationItems: items.Count(i => i.State == WorkItemState.Resolved));

        var closedAt = items.ToDictionary(
            i => i.Id,
            i => FirstEntry(timelines, i.Id, WorkItemState.Closed));

        var burndown = BuildBurndown(
            sprint,
            [.. items.Select(i => (i.Id, Points: i.StoryPoints ?? 0))],
            closedAt,
            committedPoints);

        // Items that never left New. Usually the honest answer to "why did we not finish": work that
        // was committed to and never started, rather than work that took longer than expected.
        var untouched = items.Count(i =>
            !timelines.TryGetValue(i.Id, out var changes)
            || changes.All(c => c.To == WorkItemState.New));

        return new SprintReport(summary, burndown, Aggregate(timelines), untouched);
    }

    public async Task<VelocityReport> GetVelocityAsync(
        Guid projectId, int sprintCount, CancellationToken ct = default)
    {
        var take = Math.Clamp(sprintCount, 1, MaxVelocitySprints);

        // Completed sprints only. An in-flight sprint's completed points are a partial number, and
        // charting it makes the last bar look like a collapse whenever somebody looks mid-sprint.
        var sprints = await _context.Sprints
            .Where(s => s.ProjectId == projectId && s.Status == SprintStatus.Completed)
            .OrderByDescending(s => s.EndDate)
            .Take(take)
            .Select(s => new { s.Id, s.Number, s.EndDate })
            .ToListAsync(ct);

        if (sprints.Count == 0)
            return new VelocityReport([], null, await ProjectCycleTimeAsync(projectId, ct));

        var sprintIds = sprints.Select(s => s.Id).ToList();

        var membership = await _context.SprintWorkItems
            .Where(sw => sprintIds.Contains(sw.SprintId))
            .Join(_context.WorkItems.Where(w => w.IsActive), sw => sw.WorkItemId, w => w.Id,
                (sw, w) => new { sw.SprintId, w.State, w.StoryPoints })
            .ToListAsync(ct);

        var points = sprints
            .Select(s =>
            {
                var mine = membership.Where(m => m.SprintId == s.Id).ToList();

                return new VelocityPoint(
                    s.Id, s.Number, s.EndDate,
                    CommittedPoints: mine.Sum(m => m.StoryPoints ?? 0),
                    CompletedPoints: mine
                        .Where(m => m.State == WorkItemState.Closed)
                        .Sum(m => m.StoryPoints ?? 0));
            })
            .OrderBy(p => p.EndDate)
            .ToList();

        return new VelocityReport(
            points,
            Math.Round(points.Average(p => (double)p.CompletedPoints), 2),
            await ProjectCycleTimeAsync(projectId, ct));
    }

    // ── Computation ───────────────────────────────────────────────────────────

    /// <summary>
    /// The state changes for a set of items, grouped and ordered.
    /// </summary>
    /// <remarks>
    /// One query for every item rather than one per item. Ordered by time and then by id, because
    /// changes written in one transaction share a timestamp and an unordered pair would make "first
    /// entry into Active" depend on how Postgres felt about returning them.
    /// </remarks>
    private async Task<Dictionary<Guid, List<StateChange>>> LoadTimelinesAsync(
        IEnumerable<Guid> workItemIds, CancellationToken ct)
    {
        var ids = workItemIds as List<Guid> ?? [.. workItemIds];

        if (ids.Count == 0) return [];

        var rows = await _context.WorkItemHistory
            .Where(h => ids.Contains(h.WorkItemId) && h.FieldName == "State" && h.NewValue != null)
            .OrderBy(h => h.CreatedAt)
            .ThenBy(h => h.Id)
            .Select(h => new { h.WorkItemId, h.NewValue, h.CreatedAt })
            .ToListAsync(ct);

        var timelines = new Dictionary<Guid, List<StateChange>>();

        foreach (var row in rows)
        {
            // A state name that no longer parses is a row from a vocabulary that has since changed.
            // Skipped rather than guessed at: a wrong state here silently distorts every metric.
            if (!Enum.TryParse<WorkItemState>(row.NewValue, out var state)) continue;

            if (!timelines.TryGetValue(row.WorkItemId, out var changes))
                timelines[row.WorkItemId] = changes = [];

            changes.Add(new StateChange(row.WorkItemId, state, row.CreatedAt));
        }

        return timelines;
    }

    private static DateTime? FirstEntry(
        Dictionary<Guid, List<StateChange>> timelines, Guid workItemId, WorkItemState state)
    {
        if (!timelines.TryGetValue(workItemId, out var changes)) return null;

        foreach (var change in changes)
            if (change.To == state)
                return change.At;

        return null;
    }

    private static CycleTimeMetrics Aggregate(Dictionary<Guid, List<StateChange>> timelines)
    {
        var measured = timelines.Values
            .Select(StateTimeline.Measure)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToList();

        return new CycleTimeMetrics(
            ItemsMeasured: measured.Count,
            MedianPickupHours: StateTimeline.Median(measured.Select(s => s.Pickup)),
            MedianDevelopmentHours: StateTimeline.Median(measured.Select(s => s.Development)),
            MedianVerificationWaitHours: StateTimeline.Median(measured.Select(s => s.VerificationWait)),
            MedianTotalHours: StateTimeline.Median(measured.Select(s => s.Total)));
    }

    private async Task<CycleTimeMetrics> ProjectCycleTimeAsync(Guid projectId, CancellationToken ct)
    {
        var closedItems = await _context.WorkItems
            .Where(w => w.ProjectId == projectId && w.IsActive && w.State == WorkItemState.Closed)
            .Select(w => w.Id)
            .ToListAsync(ct);

        return Aggregate(await LoadTimelinesAsync(closedItems, ct));
    }

    /// <summary>
    /// Remaining work at the end of each day of the sprint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from when each item first reached <c>Closed</c>, so the line reflects what was actually
    /// finished rather than a stored daily snapshot that only exists if a job ran every night.
    /// Recomputing from history means a burndown is correct for a sprint that ran before this feature
    /// existed.
    /// </para>
    /// <para>
    /// <b>Known limitation:</b> it uses the sprint's <em>current</em> membership, so items added or
    /// removed mid-sprint are treated as though they were always there. A real scope-change line
    /// needs membership history, which is not recorded — noted rather than approximated, because a
    /// burndown that quietly misrepresents scope change is worse than one that does not show it.
    /// </para>
    /// </remarks>
    private static List<BurndownPoint> BuildBurndown(
        Sprint sprint,
        IReadOnlyList<(Guid Id, int Points)> items,
        Dictionary<Guid, DateTime?> closedAt,
        int committedPoints)
    {
        var start = sprint.StartDate.Date;

        // Never past today: projecting a line into the future would draw a flat tail that reads as
        // "no progress" rather than "has not happened yet".
        var last = sprint.EndDate.Date;
        var today = DateTime.UtcNow.Date;
        var end = last < today ? last : today;

        if (end < start) return [];

        var totalDays = Math.Max(1, (sprint.EndDate.Date - start).Days);
        var points = new List<BurndownPoint>();

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var cutoff = day.AddDays(1);

            var outstanding = items
                .Where(i => closedAt.GetValueOrDefault(i.Id) is not { } closed || closed >= cutoff)
                .ToList();

            var elapsed = (day - start).Days;

            points.Add(new BurndownPoint(
                Date: DateTime.SpecifyKind(day, DateTimeKind.Utc),
                RemainingPoints: outstanding.Sum(i => i.Points),
                RemainingItems: outstanding.Count,
                IdealPoints: Math.Round(
                    Math.Max(0, committedPoints * (1.0 - (double)elapsed / totalDays)), 2)));
        }

        return points;
    }
}
