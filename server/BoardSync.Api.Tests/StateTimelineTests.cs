using BoardSync.Api.Modules.Reporting.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// That cycle time measures what it claims to.
/// </summary>
/// <remarks>
/// These numbers reach management, and a wrong one is worse than a missing one: nobody audits a
/// figure that looks plausible. The arithmetic is pure precisely so it can be checked without a
/// database — the same reason <c>AccessEvaluator</c> is.
/// </remarks>
public class StateTimelineTests
{
    private static readonly DateTime Origin = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Item = Guid.NewGuid();

    private static StateChange At(double hours, WorkItemState state) =>
        new(Item, state, Origin.AddHours(hours));

    // ── The spans ─────────────────────────────────────────────────────────────

    [Fact]
    public void EachStageIsMeasuredFromItsOwnTransition()
    {
        var spans = StateTimeline.Measure([
            At(0, WorkItemState.New),
            At(5, WorkItemState.Active),
            At(20, WorkItemState.InReview),
            At(29, WorkItemState.Resolved),
            At(53, WorkItemState.Closed)
        ]);

        Assert.NotNull(spans);
        Assert.Equal(5, spans!.Value.Pickup);
        Assert.Equal(24, spans.Value.Development);        // Active → Resolved, through InReview
        Assert.Equal(24, spans.Value.VerificationWait);   // Resolved → Closed
        Assert.Equal(53, spans.Value.Total);
    }

    /// <summary>
    /// An item that has not closed is not measured at all.
    /// </summary>
    /// <remarks>
    /// Counting open work as "so far" would make cycle time fall every time somebody creates a work
    /// item, which is the sort of metric that rewards the wrong behaviour.
    /// </remarks>
    [Fact]
    public void OpenWorkIsNotMeasured() =>
        Assert.Null(StateTimeline.Measure([
            At(0, WorkItemState.New),
            At(5, WorkItemState.Active),
            At(29, WorkItemState.Resolved)
        ]));

    [Fact]
    public void AnEmptyTimelineMeasuresNothing() => Assert.Null(StateTimeline.Measure([]));

    /// <summary>
    /// A stage that never happened is null, not zero.
    /// </summary>
    /// <remarks>
    /// Zero would say "this took no time", which is a claim. Null says "we cannot tell", which is
    /// the truth — and the median skips it rather than dragging the figure toward zero.
    /// </remarks>
    [Fact]
    public void AStageThatNeverHappenedIsUnknownRatherThanZero()
    {
        var spans = StateTimeline.Measure([
            At(0, WorkItemState.New),
            At(10, WorkItemState.Closed)      // reopened and closed without passing through
        ]);

        Assert.NotNull(spans);
        Assert.Null(spans!.Value.Pickup);
        Assert.Null(spans.Value.Development);
        Assert.Null(spans.Value.VerificationWait);
        Assert.Equal(10, spans.Value.Total);
    }

    /// <summary>
    /// Work that bounces is measured from its first entry into a stage, not its last.
    /// </summary>
    /// <remarks>
    /// The case that makes this worth a test. QA sending something back, or a pull request getting
    /// changes requested, means an item enters Active more than once — and measuring the last entry
    /// would report the final attempt rather than how long the work actually took.
    /// </remarks>
    [Fact]
    public void ReworkIsMeasuredFromTheFirstEntryIntoEachStage()
    {
        var spans = StateTimeline.Measure([
            At(0, WorkItemState.New),
            At(2, WorkItemState.Active),
            At(10, WorkItemState.Resolved),
            At(12, WorkItemState.Active),     // QA sent it back
            At(30, WorkItemState.Resolved),
            At(40, WorkItemState.Closed)
        ]);

        Assert.NotNull(spans);
        Assert.Equal(2, spans!.Value.Pickup);
        Assert.Equal(8, spans.Value.Development);        // first Active → first Resolved
        Assert.Equal(30, spans.Value.VerificationWait);  // first Resolved → Closed, rework included
        Assert.Equal(40, spans.Value.Total);
    }

    /// <summary>
    /// Out-of-order history reports nothing rather than a negative duration.
    /// </summary>
    /// <remarks>
    /// Reachable: git events arrive late and are recorded against the time the event happened, not
    /// the time it was written. "We cannot tell" is honest; "-4 hours" is not, and a negative in a
    /// median quietly corrupts every figure it touches.
    /// </remarks>
    [Fact]
    public void OutOfOrderHistoryYieldsNullRatherThanANegativeSpan()
    {
        var spans = StateTimeline.Measure([
            At(10, WorkItemState.New),
            At(4, WorkItemState.Active),      // recorded as earlier than creation
            At(20, WorkItemState.Closed)
        ]);

        Assert.NotNull(spans);
        Assert.Null(spans!.Value.Pickup);
        Assert.Equal(10, spans.Value.Total);
    }

    // ── The median ────────────────────────────────────────────────────────────

    [Fact]
    public void TheMedianOfAnOddCountIsTheMiddleValue() =>
        Assert.Equal(5, StateTimeline.Median([1.0, 5.0, 100.0]));

    [Fact]
    public void TheMedianOfAnEvenCountAveragesTheMiddleTwo() =>
        Assert.Equal(7.5, StateTimeline.Median([1.0, 5.0, 10.0, 100.0]));

    [Fact]
    public void UnknownsAreSkippedRatherThanCountedAsZero() =>
        Assert.Equal(5, StateTimeline.Median([null, 1.0, 5.0, null, 100.0]));

    [Fact]
    public void NothingToMeasureIsNull()
    {
        Assert.Null(StateTimeline.Median([]));
        Assert.Null(StateTimeline.Median([null, null]));
    }

    /// <summary>
    /// The median resists an outlier where a mean would not.
    /// </summary>
    /// <remarks>
    /// The reason for choosing it. One item that sat in a backlog for three months is the normal
    /// case, not a data error, and an average dragged to 750 hours is a figure people learn to
    /// ignore.
    /// </remarks>
    [Fact]
    public void OneAbandonedItemDoesNotDistortTheFigure()
    {
        double[] hours = [2, 3, 4, 5, 3000];

        Assert.Equal(4, StateTimeline.Median(hours.Select(h => (double?)h)));
        Assert.True(hours.Average() > 600);
    }
}
