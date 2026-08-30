using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Reporting.DTOs;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>
/// Turns a computed sprint report into prose.
/// </summary>
/// <remarks>
/// <para>
/// An interface because the model is the one part of this system that cannot be tested
/// deterministically. Everything around it — the budget, the grounding check, the endpoint's
/// behaviour when there is no model — is exercised against a fake, which is where the rules that
/// matter actually live.
/// </para>
/// <para>
/// <b>It receives a report and computes nothing.</b> Every figure it may state is in the object it
/// is handed, and every work item it may name is in one of the two lists beside it.
/// <c>NarrativeGuard</c> verifies both afterwards — figures against the report, references against
/// the lists — so a model that invents either is caught rather than believed.
/// </para>
/// </remarks>
public interface INarrator
{
    /// <summary>Whether a model is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Writes a narrative over the report.
    /// </summary>
    /// <returns>The prose and the tokens it cost, or null when the model could not be reached.</returns>
    Task<NarrationOutcome?> NarrateAsync(
        NarrativeInput input,
        CancellationToken ct = default);
}

/// <summary>What a narration produced and what it cost.</summary>
/// <param name="Headline">One sentence.</param>
/// <param name="Summary">Two or three.</param>
/// <param name="Observations">What stands out, each tied to a figure.</param>
/// <param name="TokensSpent">
/// Input plus output. Charged against the organization's allowance whether or not the result
/// survives the grounding check — the tokens were spent either way, and a budget that only counted
/// successes would be a budget somebody could exhaust for free.
/// </param>
/// <param name="Outcome">What the sprint set out to do, and whether it did it.</param>
/// <param name="Shipped">What reached Closed, a sentence each.</param>
/// <param name="DidNotLand">What did not land, and where it stopped.</param>
/// <param name="WhereWorkIsSitting">Where work is queuing — the QA lane, or items never started.</param>
public readonly record struct NarrationOutcome(
    string Headline,
    string Summary,
    IReadOnlyList<string> Observations,
    int TokensSpent,
    string Outcome = "",
    IReadOnlyList<string>? Shipped = null,
    IReadOnlyList<string>? DidNotLand = null,
    IReadOnlyList<string>? WhereWorkIsSitting = null);
