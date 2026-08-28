using BoardSync.Api.Modules.GitSync.Domain;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// How far forward a git event carries a work item.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a bug that stranded work permanently. A pull request opening on an item
/// still in <c>New</c> was refused as an illegal transition, and the refusal left the item in
/// <c>New</c> — so the merge that followed was refused identically, and so was everything after it.
/// A single missed push took an item out of the board's reach for good.
/// </para>
/// <para>
/// The opposite failure is worse and quieter: a walk that invents states nobody was in. Every
/// figure in the reports is reconstructed from the history rows these hops write, so a fabricated
/// "reached In Review" puts a cycle-time number on a review that never happened.
/// </para>
/// </remarks>
public class TransitionPathTests
{
    [Fact]
    public void OpeningAPullRequestOnUntouchedWorkStartsItFirst()
    {
        // The bug: New reaches only Active in one hop, so this used to be refused outright.
        var path = TransitionPath.Forward(WorkItemState.New, WorkItemState.InReview);

        Assert.Equal(
            [WorkItemState.Active, WorkItemState.InReview],
            path);
    }

    [Fact]
    public void MergingUntouchedWorkDoesNotInventAReview()
    {
        var path = TransitionPath.Forward(WorkItemState.New, WorkItemState.Resolved);

        /*
         * Active → Resolved is a legal single hop — work that needed no pull request — so the
         * shortest route is two hops, not three. Routing through InReview would be a record of a
         * review nobody did, and cycle time is computed from exactly these rows.
         */
        Assert.Equal(
            [WorkItemState.Active, WorkItemState.Resolved],
            path);

        Assert.DoesNotContain(WorkItemState.InReview, path);
    }

    [Fact]
    public void AMergeOnActiveWorkResolvesItDirectly()
    {
        Assert.Equal(
            [WorkItemState.Resolved],
            TransitionPath.Forward(WorkItemState.Active, WorkItemState.Resolved));
    }

    [Theory]
    [InlineData(WorkItemState.New, WorkItemState.Active)]
    [InlineData(WorkItemState.Active, WorkItemState.InReview)]
    [InlineData(WorkItemState.InReview, WorkItemState.Resolved)]
    public void AdjacentStatesAreASingleHop(
        WorkItemState from, WorkItemState to)
    {
        Assert.Equal([to], TransitionPath.Forward(from, to));
    }

    [Fact]
    public void NothingMovesBackwards()
    {
        // The monotonic invariant. A retried push landing after the pull request it preceded must
        // not drag a merged item back to Active.
        Assert.Empty(
            TransitionPath.Forward(WorkItemState.Resolved, WorkItemState.Active));

        Assert.Empty(
            TransitionPath.Forward(WorkItemState.Closed, WorkItemState.Active));

        Assert.Empty(
            TransitionPath.Forward(WorkItemState.InReview, WorkItemState.Active));
    }

    [Fact]
    public void AnItemAlreadyThereDoesNotMove()
    {
        Assert.Empty(
            TransitionPath.Forward(WorkItemState.InReview, WorkItemState.InReview));
    }

    [Fact]
    public void NoWalkOvershootsItsDestination()
    {
        foreach (var from in Enum.GetValues<WorkItemState>())
        foreach (var to in Enum.GetValues<WorkItemState>())
        {
            var path = TransitionPath.Forward(from, to);

            if (path.Count == 0) continue;

            Assert.Equal(to, path[^1]);

            Assert.All(path, state =>
                Assert.True(TransitionPath.Rank(state) <= TransitionPath.Rank(to)));
        }
    }

    [Fact]
    public void EveryHopIsOneTheWorkflowAllows()
    {
        foreach (var from in Enum.GetValues<WorkItemState>())
        foreach (var to in Enum.GetValues<WorkItemState>())
        {
            var path = TransitionPath.Forward(from, to);

            if (path.Count == 0) continue;

            var previous = from;

            foreach (var step in path)
            {
                /*
                 * The walk may not reach anywhere the state machine would refuse a person. If this
                 * ever fails, the automation has found a route around the QA gate.
                 */
                Assert.True(
                    WorkItemStateMachine.CanTransition(previous, step),
                    $"{previous} → {step} is not a legal transition");

                previous = step;
            }
        }
    }

    [Fact]
    public void AutomationCannotReachClosed()
    {
        /*
         * Not because this class stops it — the integration principal holds workitem:write and not
         * workitem:verify, and the permission check inside the walk refuses each hop into Closed.
         * Asserted here because a route that exists is one a widened role could take.
         */
        var path = TransitionPath.Forward(WorkItemState.New, WorkItemState.Closed);

        Assert.Equal(WorkItemState.Closed, path[^1]);

        Assert.Equal(
            WorkItemState.Resolved,
            path[^2]);
    }
}
