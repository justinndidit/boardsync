using System.Text.Json;

using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Modules.Reporting.DTOs;

namespace BoardSync.Api.Modules.Intelligence.Providers;

/// <summary>
/// Writes a sprint narrative using Gemini.
/// </summary>
/// <remarks>
/// <para>
/// The same instructions and the same output shape as the other provider — both come from
/// <see cref="IntelligencePrompts"/> and the schema below mirrors it. What differs is the dialect:
/// Gemini's schema is an OpenAPI subset that has no <c>additionalProperties</c>, so the guarantee
/// that no extra fields appear is not available here. It costs nothing — the response is
/// deserialized into a fixed shape and anything extra is ignored.
/// </para>
/// <para>
/// <b>The grounding check is unchanged and is what makes this safe to swap.</b>
/// <c>NarrativeGuard</c> verifies every figure in the prose against the report afterwards,
/// whichever model wrote it, so a provider that hallucinates a number is caught by the same rule
/// rather than trusted differently.
/// </para>
/// </remarks>
public class GeminiNarrator : INarrator
{
    private readonly GeminiClient? _client;
    private readonly ILogger<GeminiNarrator> _logger;

    /// <summary>A cap on the response, which the model does not see.</summary>
    private const int MaxTokens = 2048;

    public GeminiNarrator(
        GeminiClientFactory factory,
        ILogger<GeminiNarrator> logger)
    {
        _logger = logger;
        _client = factory.Create();
    }

    public bool IsConfigured => _client is not null;

    public async Task<NarrationOutcome?> NarrateAsync(
        SprintReport report,
        CancellationToken ct = default)
    {
        if (_client is null) return null;

        try
        {
            var answer = await _client.GenerateAsync(
                IntelligencePrompts.Narrator,
                $"Sprint report:\n{JsonSerializer.Serialize(report)}",
                Schema,
                MaxTokens,
                ct);

            if (answer is not { } written) return null;

            var parsed = JsonSerializer.Deserialize<NarrativeShape>(
                written.Text,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (parsed is null) return null;

            return new NarrationOutcome(
                parsed.Headline ?? "",
                parsed.Summary ?? "",
                parsed.Observations ?? [],
                written.TokensSpent);
        }
        catch (Exception ex)
        {
            // "No narrative", not a failed request. The report is already computed and is the thing
            // the caller asked for; losing the prose is a degradation.
            _logger.LogWarning(ex,
                "Narration failed for sprint {SprintId}", report.Summary.SprintId);

            return null;
        }
    }

    /// <summary>
    /// The shape the model must answer in.
    /// </summary>
    /// <remarks>
    /// <c>propertyOrdering</c> rather than relying on declaration order: Gemini emits fields in the
    /// order given, and a stable order is what makes two runs over the same report comparable.
    /// </remarks>
    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new
        {
            headline = new { type = "STRING" },
            summary = new { type = "STRING" },
            observations = new
            {
                type = "ARRAY",
                items = new { type = "STRING" },
            },
        },
        required = new[] { "headline", "summary", "observations" },
        propertyOrdering = new[] { "headline", "summary", "observations" },
    };

    private sealed record NarrativeShape(
        string? Headline,
        string? Summary,
        List<string>? Observations);
}
