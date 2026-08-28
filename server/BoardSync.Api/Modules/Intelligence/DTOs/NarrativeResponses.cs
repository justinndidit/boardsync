namespace BoardSync.Api.Modules.Intelligence.DTOs;

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
public record SprintNarrative(
    string Headline,
    string Summary,
    IReadOnlyList<string> Observations,
    bool Grounded,
    IReadOnlyList<string> UnsupportedClaims);

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
