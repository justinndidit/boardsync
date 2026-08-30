using BoardSync.Api.Modules.Reporting.DTOs;

namespace BoardSync.Api.Modules.Intelligence.DTOs;

/// <summary>
/// One work item, as the narrator is told about it.
/// </summary>
/// <remarks>
/// The reference and the title, nothing else. The narrator was previously handed aggregate figures
/// alone and so could not name a single thing the team had built — which is why its reports read as
/// arithmetic rather than as an account of a sprint.
/// </remarks>
public record NarratedItem(string Reference, string Title, string State);

/// <summary>
/// What the narrator is given: the computed figures, and what the work actually was.
/// </summary>
/// <remarks>
/// <para>
/// <b>It still computes nothing.</b> Every figure is in <paramref name="Report"/> and every item it
/// may name is in one of the two lists. <c>NarrativeGuard</c> checks both afterwards — figures
/// against the report, references against these lists — so a model that invents either is caught
/// rather than believed.
/// </para>
/// <para>
/// The lists are capped where they are built. A sprint of two hundred items would otherwise put two
/// hundred titles in a prompt to produce a paragraph, and a report that lists everything is not a
/// report.
/// </para>
/// </remarks>
public record NarrativeInput(
    SprintReport Report,
    IReadOnlyList<NarratedItem> Delivered,
    IReadOnlyList<NarratedItem> Unfinished);

/// <summary>
/// A written account of a sprint, over figures somebody else computed.
/// </summary>
/// <param name="Headline">One sentence a reader could repeat in a standup.</param>
/// <param name="Summary">Two or three sentences on what happened.</param>
/// <param name="Observations">
/// What stands out, each tied to a figure. Empty when nothing does — a report with nothing to say
/// should say nothing rather than fill the space.
/// </param>
/// <param name="Grounded">
/// Whether every figure in the prose appears in the report it was written from.
/// </param>
/// <param name="UnsupportedClaims">
/// Sentences that stated a figure the report does not contain, when there are any.
///
/// **Surfaced rather than hidden.** A narrative that failed the check is not returned as prose; a
/// caller gets the figures back and the reason. Quietly dropping the offending sentence would leave
/// a paragraph that reads fine and no longer says what the model meant.
/// </param>
/// <param name="Outcome">What the sprint set out to do, and whether it did it.</param>
/// <param name="Shipped">What reached Closed, a sentence each. Empty when nothing did.</param>
/// <param name="DidNotLand">What did not land, and where it stopped.</param>
/// <param name="WhereWorkIsSitting">
/// Where work is queuing — the QA lane, or items never picked up.
///
/// Its own section because the two have different owners: work finished and waiting to be tested
/// is a QA queue, not slow development, and a report that blurs them sends somebody to the wrong
/// conversation.
/// </param>
public record SprintNarrative(
    string Headline,
    string Summary,
    IReadOnlyList<string> Observations,
    bool Grounded,
    IReadOnlyList<string> UnsupportedClaims,
    string Outcome = "",
    IReadOnlyList<string>? Shipped = null,
    IReadOnlyList<string>? DidNotLand = null,
    IReadOnlyList<string>? WhereWorkIsSitting = null);

/// <summary>Why a narrative could not be produced.</summary>
public enum NarrativeUnavailable
{
    /// <summary>No model is configured. The rest of the product works without one.</summary>
    NotConfigured,

    /// <summary>The organization has spent its allowance for the period.</summary>
    BudgetExhausted,

    /// <summary>The model was reachable and the answer failed the grounding check.</summary>
    NotGrounded,

    /// <summary>The model could not be reached.</summary>
    ProviderError
}

/// <summary>A narrative, or the reason there isn't one.</summary>
public readonly record struct NarrativeResult(
    SprintNarrative? Narrative,
    NarrativeUnavailable? Unavailable,
    string? Detail)
{
    public static NarrativeResult Ok(SprintNarrative narrative) => new(narrative, null, null);

    public static NarrativeResult No(NarrativeUnavailable reason, string detail) =>
        new(null, reason, detail);
}
