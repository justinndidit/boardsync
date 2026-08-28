using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Reporting.DTOs;
using BoardSync.Api.Modules.Reporting.Services;

namespace BoardSync.Api.Modules.Intelligence.Services;

public interface INarrativeService
{
    /// <summary>Writes a narrative over a sprint's computed report, or explains why it cannot.</summary>
    Task<NarrativeResult> ForSprintAsync(
        Guid sprintId, Guid organizationId, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Everything that decides whether a narrative ships lives here, and none of it is the model:
/// the organization's allowance, the grounding check, and what a caller gets when either says no.
/// The model is behind <see cref="INarrator"/> so all of it can be tested.
/// </para>
/// <para>
/// <b>This module computes nothing.</b> It asks <c>Reporting</c> for the figures and passes them
/// on — see <c>build_context.md</c> §8.3. The separation is what lets a reader know which numbers
/// on a page were calculated.
/// </para>
/// </remarks>
public sealed class NarrativeService : INarrativeService
{
    private readonly IReportingService _reporting;
    private readonly INarrator _narrator;
    private readonly ITokenBudget _budget;
    private readonly ILogger<NarrativeService> _logger;

    public NarrativeService(
        IReportingService reporting,
        INarrator narrator,
        ITokenBudget budget,
        ILogger<NarrativeService> logger)
    {
        _reporting = reporting;
        _narrator = narrator;
        _budget = budget;
        _logger = logger;
    }

    public async Task<NarrativeResult> ForSprintAsync(
        Guid sprintId, Guid organizationId, CancellationToken ct = default)
    {
        if (!_narrator.IsConfigured)
        {
            return NarrativeResult.No(
                NarrativeUnavailable.NotConfigured,
                "No language model is configured. Every figure on the report is still computed.");
        }

        // Checked before the call, not after. A budget enforced on the way out has already spent
        // the money it exists to cap.
        if (!await _budget.HasRemainingAsync(organizationId, ct))
        {
            return NarrativeResult.No(
                NarrativeUnavailable.BudgetExhausted,
                "This organization has used its narrative allowance for the period.");
        }

        var report = await _reporting.GetSprintReportAsync(sprintId, ct);

        var outcome = await _narrator.NarrateAsync(report, ct);

        if (outcome is not { } written)
        {
            return NarrativeResult.No(
                NarrativeUnavailable.ProviderError,
                "The narrative could not be generated. The report itself is unaffected.");
        }

        // Recorded whether or not the result survives the check below: the tokens were spent, and
        // an allowance that only counted successes is one somebody could exhaust for free.
        await _budget.RecordAsync(organizationId, written.TokensSpent, ct);

        var supported = FiguresIn(report);

        var prose = new[] { written.Headline, written.Summary }
            .Concat(written.Observations)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var unsupported = prose
            .SelectMany(sentence =>
                NarrativeGuard.UnsupportedClaims(sentence, supported))
            .ToList();

        if (unsupported.Count > 0)
        {
            /*
             * Withheld rather than trimmed. Dropping the offending sentence would leave a paragraph
             * that reads perfectly well and no longer says what was meant — and a reader has no way
             * to tell that something was removed. Returning the reason keeps the failure visible.
             */
            _logger.LogWarning(
                "Narrative for sprint {SprintId} cited {Count} figures not in its report: {Figures}",
                sprintId,
                unsupported.Count,
                string.Join(", ", unsupported.Select(u => u.Figure)));

            return NarrativeResult.Ok(new SprintNarrative(
                Headline: "",
                Summary: "",
                Observations: [],
                Grounded: false,
                UnsupportedClaims: [.. unsupported.Select(u => u.Sentence)]));
        }

        return NarrativeResult.Ok(new SprintNarrative(
            written.Headline,
            written.Summary,
            written.Observations,
            Grounded: true,
            UnsupportedClaims: []));
    }

    /// <summary>
    /// Every number the report contains.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A figure missed here is a true sentence rejected, and a guard that
    /// rejects true sentences is one people learn to switch off.
    /// </remarks>
    private static IReadOnlyCollection<double> FiguresIn(SprintReport report)
    {
        var s = report.Summary;

        var figures = new List<double>
        {
            s.Number,
            s.CommittedPoints, s.CompletedPoints,
            s.CommittedItems, s.CompletedItems,
            s.AwaitingVerificationItems,
            report.ItemsWithNoActivity,
            report.CycleTime.ItemsMeasured,

            // Derived figures a narrator may reasonably state: the shortfall and the remainder are
            // subtraction anybody would do out loud, and rejecting them would make the prose
            // stilted without making it truer.
            s.CommittedPoints - s.CompletedPoints,
            s.CommittedItems - s.CompletedItems,
            0,
        };

        foreach (var median in new[]
                 {
                     report.CycleTime.MedianPickupHours,
                     report.CycleTime.MedianDevelopmentHours,
                     report.CycleTime.MedianVerificationWaitHours,
                     report.CycleTime.MedianTotalHours,
                 })
        {
            if (median is { } value) figures.Add(value);
        }

        foreach (var point in report.Burndown)
        {
            figures.Add(point.RemainingPoints);
            figures.Add(point.RemainingItems);
            figures.Add(point.IdealPoints);
        }

        if (s.CommittedPoints > 0)
        {
            // The completion percentage, which is the one ratio a reader expects in a summary.
            figures.Add(Math.Round(
                s.CompletedPoints * 100.0 / s.CommittedPoints, 1));
        }

        return figures;
    }
}
