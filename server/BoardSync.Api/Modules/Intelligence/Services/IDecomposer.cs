using BoardSync.Api.Modules.Intelligence.DTOs;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>
/// Turns a product requirements document into a proposed work item hierarchy.
/// </summary>
/// <remarks>
/// An interface for the same reason <see cref="INarrator"/> is one: the model call is the only part
/// that cannot be tested deterministically, so everything around it — the guard, the budget, the
/// acceptance rules — is exercised against a fake.
/// </remarks>
public interface IDecomposer
{
    /// <summary>Whether a model is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Proposes a breakdown of <paramref name="document"/>.
    /// </summary>
    /// <returns>The draft and what it cost, or null when the model could not be reached.</returns>
    Task<DecompositionOutcome?> DecomposeAsync(
        string document,
        CancellationToken ct = default);
}

/// <summary>What a decomposition produced and what it cost.</summary>
/// <param name="Draft">
/// As the model returned it — unchecked. <see cref="Domain.DecompositionGuard"/> is what decides
/// whether it is fit to show anybody.
/// </param>
/// <param name="TokensSpent">
/// Input plus output, charged whether or not the draft survives checking. The tokens were spent
/// either way, and a budget that only counted usable answers is one somebody could exhaust for free.
/// </param>
public readonly record struct DecompositionOutcome(
    Decomposition Draft,
    int TokensSpent);
