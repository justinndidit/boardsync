namespace BoardSync.Api.Modules.Reporting.DTOs;

/// <summary>
/// One day of a sprint burndown.
/// </summary>
/// <param name="Date">The day, at UTC midnight.</param>
/// <param name="RemainingPoints">
/// Story points not yet closed at the end of that day. Items carrying no estimate count zero, which
/// is why <paramref name="RemainingItems"/> is reported alongside.
/// </param>
/// <param name="RemainingItems">Items not yet closed at the end of that day.</param>
/// <param name="IdealPoints">
/// Where a perfectly even burn would be. A reference line, not a target — its only job is to make
/// the shape of the real line legible.
/// </param>
public record BurndownPoint(DateTime Date, int RemainingPoints, int RemainingItems, double IdealPoints);

/// <summary>
/// How long work spent in each part of the workflow.
/// </summary>
/// <remarks>
/// <para>
/// Reported as medians rather than means. One item that sat in a backlog for three months drags an
/// average somewhere nobody recognises, and cycle-time distributions are reliably skewed that way.
/// </para>
/// <para>
/// Every span is reconstructed from <c>WorkItemHistory</c>, so it measures when the board says
/// something happened. That is a stronger claim than it used to be: the board moves itself from git,
/// so "reached In Review" is a pull request opening rather than somebody remembering to drag a card.
/// </para>
/// </remarks>
/// <param name="ItemsMeasured">How many closed items these figures are drawn from.</param>
/// <param name="MedianPickupHours">New → Active: how long work waited before anybody started it.</param>
/// <param name="MedianDevelopmentHours">Active → Resolved: how long the work itself took.</param>
/// <param name="MedianVerificationWaitHours">
/// Resolved → Closed: <b>how long finished work waited to be tested.</b> The metric BoardSync can
/// report and a hand-updated board cannot, because the QA gate makes it a real transition rather
/// than a convention.
/// </param>
/// <param name="MedianTotalHours">New → Closed, end to end.</param>
public record CycleTimeMetrics(
    int ItemsMeasured,
    double? MedianPickupHours,
    double? MedianDevelopmentHours,
    double? MedianVerificationWaitHours,
    double? MedianTotalHours);

/// <summary>What one sprint delivered.</summary>
/// <param name="SprintId">The sprint.</param>
/// <param name="Number">Its number within the project.</param>
/// <param name="Goal">Its goal, if it has one.</param>
/// <param name="StartDate">When it started.</param>
/// <param name="EndDate">When it ends or ended.</param>
/// <param name="Status">Planning, Active or Completed.</param>
/// <param name="CommittedPoints">Story points of everything in the sprint.</param>
/// <param name="CompletedPoints">Story points of what reached Closed.</param>
/// <param name="CommittedItems">How many items are in the sprint.</param>
/// <param name="CompletedItems">How many reached Closed.</param>
/// <param name="AwaitingVerificationItems">
/// How many are sitting in the QA lane right now — merged, waiting to be tested. Worth its own
/// number: a sprint that looks behind may be finished work nobody has verified.
/// </param>
public record SprintSummary(
    Guid SprintId,
    int Number,
    string? Goal,
    DateTime StartDate,
    DateTime EndDate,
    string Status,
    int CommittedPoints,
    int CompletedPoints,
    int CommittedItems,
    int CompletedItems,
    int AwaitingVerificationItems);

/// <summary>Everything computable about one sprint.</summary>
/// <remarks>
/// <b>Every number here is computed, never generated.</b> When the narrative layer lands it will
/// receive this object and be instructed to cite only these figures — a model asked to both compute
/// and narrate produces plausible numbers, and nobody downstream can tell which were which.
/// </remarks>
/// <param name="Summary">The headline figures.</param>
/// <param name="Burndown">Remaining work per day.</param>
/// <param name="CycleTime">How long work spent in each stage.</param>
/// <param name="ItemsWithNoActivity">
/// Items in the sprint that never left New. Usually the honest answer to "why did we not finish".
/// </param>
public record SprintReport(
    SprintSummary Summary,
    IReadOnlyList<BurndownPoint> Burndown,
    CycleTimeMetrics CycleTime,
    int ItemsWithNoActivity);

/// <summary>One sprint's contribution to a velocity series.</summary>
/// <param name="SprintId">The sprint.</param>
/// <param name="Number">Its number.</param>
/// <param name="EndDate">When it ended.</param>
/// <param name="CommittedPoints">What it took on.</param>
/// <param name="CompletedPoints">What it finished.</param>
public record VelocityPoint(
    Guid SprintId, int Number, DateTime EndDate, int CommittedPoints, int CompletedPoints);

/// <summary>A project's delivery history.</summary>
/// <remarks>
/// Completed sprints only. An in-flight sprint's completed points are a partial number, and mixing
/// it into a velocity series makes the last bar look like a collapse every time somebody opens the
/// page mid-sprint.
/// </remarks>
/// <param name="Sprints">Completed sprints, oldest first.</param>
/// <param name="AverageCompletedPoints">
/// Mean completed points across them, or null when there are none. Deliberately a mean here rather
/// than a median: velocity is used for forecasting, and a forecast wants the average.
/// </param>
/// <param name="CycleTime">Cycle time across everything closed in the project.</param>
public record VelocityReport(
    IReadOnlyList<VelocityPoint> Sprints,
    double? AverageCompletedPoints,
    CycleTimeMetrics CycleTime);
