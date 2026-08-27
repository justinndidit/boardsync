using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Modules.Reporting.DTOs;
using BoardSync.Api.Modules.Reporting.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoardSync.Api.Tests;

/// <summary>
/// The rules around the model, tested without one.
/// </summary>
/// <remarks>
/// Everything that decides whether a narrative reaches a reader is deterministic — the allowance,
/// the grounding check, and what happens when either says no. Only the prose itself is not, and it
/// is behind an interface so the rest can be held to account.
/// </remarks>
public class NarrativeServiceTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static SprintReport Report() => new(
        Summary: new SprintSummary(
            SprintId: Guid.NewGuid(),
            Number: 12,
            Goal: "ship it",
            StartDate: DateTime.UtcNow.Date,
            EndDate: DateTime.UtcNow.Date.AddDays(7),
            Status: "Active",
            CommittedPoints: 40,
            CompletedPoints: 34,
            CommittedItems: 10,
            CompletedItems: 8,
            AwaitingVerificationItems: 2),
        Burndown: [],
        CycleTime: new CycleTimeMetrics(8, 1, 4, 2.5, 9),
        ItemsWithNoActivity: 0);

    private sealed class FakeNarrator : INarrator
    {
        public bool IsConfigured { get; init; } = true;
        public NarrationOutcome? Outcome { get; init; }
        public int Calls { get; private set; }

        public Task<NarrationOutcome?> NarrateAsync(
            SprintReport report, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeReporting : IReportingService
    {
        public Task<SprintReport> GetSprintReportAsync(Guid sprintId, CancellationToken ct = default) =>
            Task.FromResult(Report());

        public Task<VelocityReport> GetTeamVelocityAsync(
            Guid teamId, int sprintCount, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VelocityReport> GetVelocityForProjectAsync(
            Guid projectId, int sprintCount, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static NarrativeService Service(
        INarrator narrator, ITokenBudget? budget = null) =>
        new(new FakeReporting(),
            narrator,
            budget ?? new InMemoryTokenBudget(
                new ConfigurationBuilder().Build()),
            NullLogger<NarrativeService>.Instance);

    /// <summary>
    /// No model configured is a normal answer, not an error. Every figure on the report is still
    /// computed, and a deployment without a key should see no narrative rather than a failure.
    /// </summary>
    [Fact]
    public async Task WithoutAModelTheReportStillStands()
    {
        var narrator = new FakeNarrator { IsConfigured = false };

        var result = await Service(narrator).ForSprintAsync(Guid.NewGuid(), Org);

        Assert.Null(result.Narrative);
        Assert.Equal(NarrativeUnavailable.NotConfigured, result.Unavailable);

        // And the model was never called, so nothing was spent finding out.
        Assert.Equal(0, narrator.Calls);
    }

    [Fact]
    public async Task AGroundedNarrativeIsReturned()
    {
        var narrator = new FakeNarrator
        {
            Outcome = new NarrationOutcome(
                "34 of 40 points landed.",
                "Two items are waiting on QA.",
                ["Median wait for QA was 2.5 hours."],
                TokensSpent: 900),
        };

        var result = await Service(narrator).ForSprintAsync(Guid.NewGuid(), Org);

        Assert.NotNull(result.Narrative);
        Assert.True(result.Narrative!.Grounded);
        Assert.Empty(result.Narrative.UnsupportedClaims);
    }

    /// <summary>
    /// The failure this module exists to prevent: a plausible number nobody gave it.
    /// </summary>
    /// <remarks>
    /// The prose is withheld rather than trimmed. Removing the sentence would leave a paragraph
    /// that reads perfectly and no longer says what was meant, with nothing to signal the edit.
    /// </remarks>
    [Fact]
    public async Task AnInventedFigureWithholdsTheProse()
    {
        var narrator = new FakeNarrator
        {
            Outcome = new NarrationOutcome(
                "34 of 40 points landed.",
                "Velocity is up from 28 last sprint.",
                [],
                TokensSpent: 900),
        };

        var result = await Service(narrator).ForSprintAsync(Guid.NewGuid(), Org);

        Assert.NotNull(result.Narrative);
        Assert.False(result.Narrative!.Grounded);

        Assert.Equal("", result.Narrative.Headline);
        Assert.Equal("", result.Narrative.Summary);

        Assert.Contains(
            result.Narrative.UnsupportedClaims,
            claim => claim.Contains("28"));
    }

    /// <summary>
    /// Tokens are charged even when the answer is thrown away — they were spent, and an allowance
    /// that only counted successes is one somebody could exhaust for free.
    /// </summary>
    [Fact]
    public async Task AnUngroundedAnswerStillCostsItsTokens()
    {
        var budget = new InMemoryTokenBudget(
            new ConfigurationBuilder().Build());

        var before = await budget.RemainingAsync(Org);

        var narrator = new FakeNarrator
        {
            Outcome = new NarrationOutcome(
                "Velocity reached 77.", "", [], TokensSpent: 1_500),
        };

        await Service(narrator, budget).ForSprintAsync(Guid.NewGuid(), Org);

        Assert.Equal(before - 1_500, await budget.RemainingAsync(Org));
    }

    /// <summary>
    /// The allowance is checked before the call, not after — one enforced on the way out has
    /// already spent the money it exists to cap.
    /// </summary>
    [Fact]
    public async Task AnExhaustedAllowanceStopsTheCall()
    {
        var budget = new InMemoryTokenBudget(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Intelligence:DailyTokenLimit"] = "100",
                })
                .Build());

        await budget.RecordAsync(Org, 100);

        var narrator = new FakeNarrator
        {
            Outcome = new NarrationOutcome("fine", "fine", [], 10),
        };

        var result = await Service(narrator, budget).ForSprintAsync(Guid.NewGuid(), Org);

        Assert.Equal(NarrativeUnavailable.BudgetExhausted, result.Unavailable);
        Assert.Equal(0, narrator.Calls);
    }

    /// <summary>One organization's spending does not consume another's.</summary>
    [Fact]
    public async Task AllowancesAreHeldPerOrganization()
    {
        var budget = new InMemoryTokenBudget(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Intelligence:DailyTokenLimit"] = "100",
                })
                .Build());

        await budget.RecordAsync(Org, 100);

        Assert.False(await budget.HasRemainingAsync(Org));
        Assert.True(await budget.HasRemainingAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// An unreachable provider degrades to no narrative. The report was already computed; losing
    /// the prose is not a failed request.
    /// </summary>
    [Fact]
    public async Task AnUnreachableModelDegradesRatherThanFailing()
    {
        var result = await Service(new FakeNarrator { Outcome = null })
            .ForSprintAsync(Guid.NewGuid(), Org);

        Assert.Null(result.Narrative);
        Assert.Equal(NarrativeUnavailable.ProviderError, result.Unavailable);
    }
}
