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

    Task<VelocityReport> GetTeamVelocityAsync(
        Guid teamId, int sprintCount, CancellationToken ct = default);

    /// <summary>The velocity of the team that builds a project.</summary>
    Task<VelocityReport> GetVelocityForProjectAsync(
        Guid projectId, int sprintCount, CancellationToken ct = default);

    /// <summary>How a project's work has been distributed across states, day by day.</summary>
    Task<CumulativeFlowReport> GetCumulativeFlowAsync(
        Guid projectId, int days, CancellationToken ct = default);
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

    /// <summary>
    /// The longest cumulative-flow window.
    /// </summary>
    /// <remarks>
    /// The series is reconstructed by replaying every item's transitions against every day, so its
    /// cost is items times days. A quarter is long enough to show a queue forming and short enough
    /// that a busy project does not turn one chart into the slowest request in the product.
    /// </remarks>
    public const int MaxCumulativeFlowDays = 90;

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

        /*
         * Delivered *by the time the sprint ended*, not "closed by now".
         *
         * This counted current state, and the burndown beside it has always counted closures
         * against the day they happened — so on one page a reader could compute delivered points
         * two ways and get two answers, with nothing saying which was which.
         *
         * It also meant a finished sprint's numbers kept moving: closing a stale item three weeks
         * later silently raised a past sprint, and a chart that read 24 last week read 29 today with
         * no event to explain it. Velocity is the one figure people forecast from, and forecasting
         * from a number that includes work delivered outside the window flatters exactly the teams
         * that habitually carry work over.
         *
         * Active sprints are unaffected: their end date is in the future, so "closed by then" and
         * "closed now" are the same set.
         */
        var closed = items
            .Where(i => DeliveredBy(
                FirstEntry(timelines, i.Id, WorkItemState.Closed), sprint.EndDate))
            .ToList();

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

            /*
             * Its own number because a sprint that looks behind may be finished work nobody has
             * verified — a different problem with a different owner.
             *
             * Deliberately still *current* state, unlike the two above. "How much is sitting in QA"
             * is a question about now: on a running sprint that is exactly right, and on a finished
             * one it answers "what did this sprint leave behind that is still waiting", which is
             * also a live question. Freezing it at the end date would answer neither.
             */
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

    /// <summary>
    /// Velocity for a team, across every project it serves.
    /// </summary>
    /// <remarks>
    /// <b>A team measure, necessarily.</b> A sprint spans the team's projects, so its completed
    /// points belong to the team rather than any one project — see
    /// <c>docs/adr-001-team-sprints.md</c>. Asking per project would have to either double-count a
    /// sprint across the projects it touched or attribute it to one of them arbitrarily.
    /// </remarks>
    public async Task<VelocityReport> GetTeamVelocityAsync(
        Guid teamId, int sprintCount, CancellationToken ct = default)
    {
        var take = Math.Clamp(sprintCount, 1, MaxVelocitySprints);

        // Completed sprints only. An in-flight sprint's completed points are a partial number, and
        // charting it makes the last bar look like a collapse whenever somebody looks mid-sprint.
        var sprints = await _context.Sprints
            .Where(s => s.TeamId == teamId && s.Status == SprintStatus.Completed)
            .OrderByDescending(s => s.EndDate)
            .Take(take)
            .Select(s => new { s.Id, s.Number, s.EndDate })
            .ToListAsync(ct);

        if (sprints.Count == 0)
            return new VelocityReport([], null, await TeamCycleTimeAsync(teamId, ct));

        var sprintIds = sprints.Select(s => s.Id).ToList();

        var membership = await _context.SprintWorkItems
            .Where(sw => sprintIds.Contains(sw.SprintId))
            .Join(_context.WorkItems.Where(w => w.IsActive), sw => sw.WorkItemId, w => w.Id,
                (sw, w) => new { sw.SprintId, WorkItemId = w.Id, w.StoryPoints })
            .ToListAsync(ct);

        /*
         * One history read for every item across every charted sprint, rather than current state.
         *
         * The cost is the query the sprint report already makes; what it buys is that a completed
         * sprint's bar never moves again. Counting `State == Closed` meant closing a stale item long
         * afterwards raised a past sprint retroactively — and velocity is the figure teams plan
         * from, so it is the one that must hold still.
         */
        var timelines = await LoadTimelinesAsync(
            membership.Select(m => m.WorkItemId).Distinct(), ct);

        var points = sprints
            .Select(s =>
            {
                var mine = membership.Where(m => m.SprintId == s.Id).ToList();

                return new VelocityPoint(
                    s.Id, s.Number, s.EndDate,
                    CommittedPoints: mine.Sum(m => m.StoryPoints ?? 0),
                    CompletedPoints: mine
                        .Where(m => DeliveredBy(
                            FirstEntry(timelines, m.WorkItemId, WorkItemState.Closed),
                            s.EndDate))
                        .Sum(m => m.StoryPoints ?? 0));
            })
            .OrderBy(p => p.EndDate)
            .ToList();

        return new VelocityReport(
            points,
            Math.Round(points.Average(p => (double)p.CompletedPoints), 2),
            await TeamCycleTimeAsync(teamId, ct));
    }

    public async Task<VelocityReport> GetVelocityForProjectAsync(
        Guid projectId, int sprintCount, CancellationToken ct = default)
    {
        var teamId = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.AssignedTeamId)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Project", projectId);

        return await GetTeamVelocityAsync(teamId, sprintCount, ct);
    }

    /// <inheritdoc />
    public async Task<CumulativeFlowReport> GetCumulativeFlowAsync(
        Guid projectId, int days, CancellationToken ct = default)
    {
        if (!await _context.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new NotFoundException("Project", projectId);

        var window = Math.Clamp(days, 1, MaxCumulativeFlowDays);

        var today = DateTime.UtcNow.Date;
        var start = today.AddDays(-(window - 1));

        /*
         * Created on or before the last day in the window. An item made tomorrow has no place on a
         * chart of what has happened, and one made after the window still belongs to the days after
         * it was created — which the per-day check below handles.
         */
        var items = await _context.WorkItems
            .Where(w => w.ProjectId == projectId && w.IsActive)
            .Select(w => new { w.Id, w.CreatedAt })
            .ToListAsync(ct);

        if (items.Count == 0)
            return new CumulativeFlowReport([], 0);

        var timelines = await LoadTimelinesAsync(items.Select(i => i.Id), ct);

        var points = new List<CumulativeFlowPoint>(window);

        for (var day = start; day <= today; day = day.AddDays(1))
        {
            // End of this day, so a transition made at any hour of it is counted on it.
            var cutoff = day.AddDays(1);

            var counts = new Dictionary<WorkItemState, int>();

            foreach (var item in items)
            {
                // Not yet created is not the same as New: an item that did not exist should not be
                // holding up the bottom band on days before somebody wrote it down.
                if (item.CreatedAt >= cutoff) continue;

                var state = StateAt(timelines, item.Id, cutoff);

                counts[state] = counts.GetValueOrDefault(state) + 1;
            }

            points.Add(new CumulativeFlowPoint(
                Date: DateTime.SpecifyKind(day, DateTimeKind.Utc),
                New: counts.GetValueOrDefault(WorkItemState.New),
                Active: counts.GetValueOrDefault(WorkItemState.Active),
                InReview: counts.GetValueOrDefault(WorkItemState.InReview),
                Resolved: counts.GetValueOrDefault(WorkItemState.Resolved),
                Closed: counts.GetValueOrDefault(WorkItemState.Closed)));
        }

        return new CumulativeFlowReport(points, items.Count);
    }

    /// <summary>
    /// The state a work item stood in immediately before <paramref name="cutoff"/>.
    /// </summary>
    /// <remarks>
    /// The last transition recorded before the cutoff, or <c>New</c> when there is none — an item
    /// with no history has never moved, which is exactly what New means. Timelines are ordered by
    /// time on the way out of <see cref="LoadTimelinesAsync"/>, so this is a walk rather than a sort.
    /// </remarks>
    private static WorkItemState StateAt(
        Dictionary<Guid, List<StateChange>> timelines, Guid workItemId, DateTime cutoff)
    {
        if (!timelines.TryGetValue(workItemId, out var changes)) return WorkItemState.New;

        var state = WorkItemState.New;

        foreach (var change in changes)
        {
            if (change.At >= cutoff) break;

            state = change.To;
        }

        return state;
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

    /// <summary>
    /// Whether a closure counts toward a sprint: it happened, and it happened by the sprint's end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule the burndown applies day by day, so a sprint's completed points and the point
    /// its burndown lands on are the same number rather than two that usually agree.
    /// </para>
    /// <para>
    /// <b>One bound, not two.</b> An item added to a sprint already closed still counts. A floor at
    /// the start date would drop work legitimately finished on the first morning of a sprint it was
    /// committed to, which is a worse answer than the rare oddity it would prevent.
    /// </para>
    /// </remarks>
    private static bool DeliveredBy(DateTime? closedAt, DateTime sprintEnd) =>
        closedAt is { } closed && closed <= sprintEnd;

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

    /// <summary>
    /// Cycle time across every project the team serves.
    /// </summary>
    /// <remarks>
    /// Scoped to the team to match the velocity beside it. A per-project figure would answer a
    /// different question from the sprints it sits next to, and two numbers on one page that mean
    /// different things is how a report stops being trusted.
    /// </remarks>
    private async Task<CycleTimeMetrics> TeamCycleTimeAsync(Guid teamId, CancellationToken ct)
    {
        var closedItems = await _context.WorkItems
            .Where(w => w.IsActive && w.State == WorkItemState.Closed)
            .Join(_context.Projects.Where(p => p.AssignedTeamId == teamId),
                w => w.ProjectId, p => p.Id, (w, _) => w.Id)
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
