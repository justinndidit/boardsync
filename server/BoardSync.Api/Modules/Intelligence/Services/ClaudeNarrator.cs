using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Domain;
using System.Text.Json;

using Anthropic;
using Anthropic.Models.Messages;

using BoardSync.Api.Modules.Reporting.DTOs;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Structured output rather than prose to parse: the response is a typed object with a headline, a
/// summary and observations, so nothing here has to guess where one section ends and the next
/// begins. <c>build_context.md</c> §8 asked for this specifically.
/// </para>
/// <para>
/// The report is serialized into the prompt as JSON. That is the whole input — the model is given
/// the figures and told to write about them, and <see cref="Domain.NarrativeGuard"/> checks
/// afterwards that it used no others.
/// </para>
/// </remarks>
public sealed class ClaudeNarrator : INarrator
{
    private readonly AnthropicClient? _client;
    private readonly ILogger<ClaudeNarrator> _logger;

    /// <summary>
    /// Enough for a headline, a short summary and a few observations, and not enough for an essay.
    /// </summary>
    /// <remarks>
    /// A cap on the response, which the model does not see. The organization's allowance is the
    /// budget it is actually paced against.
    /// </remarks>
    private const int MaxTokens = 2048;

    public ClaudeNarrator(IConfiguration configuration, ILogger<ClaudeNarrator> logger)
    {
        _logger = logger;

        var apiKey = configuration["Intelligence:AnthropicApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        // Absent rather than broken. Reporting works without a model, and a deployment that has not
        // configured one should not see errors on every request — it should see no narrative.
        _client = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new AnthropicClient { ApiKey = apiKey };
    }

    public bool IsConfigured => _client is not null;

    public async Task<NarrationOutcome?> NarrateAsync(
        NarrativeInput input,
        CancellationToken ct = default)
    {
        if (_client is null) return null;

        var figures = JsonSerializer.Serialize(
            input, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = "claude-opus-5",
                MaxTokens = MaxTokens,

                // Adaptive thinking with medium effort: reading a dozen figures and deciding which
                // two matter is not a hard reasoning problem, and the cost lands on every sprint
                // report anybody opens.
                Thinking = new ThinkingConfigAdaptive(),

                OutputConfig = new OutputConfig
                {
                    Effort = Effort.Medium,
                    Format = Schema(),
                },

                System = IntelligencePrompts.Narrator,

                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = $"Sprint:\n{figures}",
                    },
                ],
            }, cancellationToken: ct);

            // ContentBlock is a union; thinking blocks precede the text one.
            var text = response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(block => block.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

            if (string.IsNullOrWhiteSpace(text)) return null;

            var parsed = JsonSerializer.Deserialize<NarrativeShape>(text);

            if (parsed is null) return null;

            return new NarrationOutcome(
                parsed.Headline ?? "",
                parsed.Summary ?? "",
                parsed.Observations ?? [],
                (int)((response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0)),
                parsed.Outcome ?? "",
                parsed.Shipped ?? [],
                parsed.DidNotLand ?? [],
                parsed.WhereWorkIsSitting ?? []);
        }
        catch (Exception ex)
        {
            // Reported as "no narrative", not as a failed request. The sprint report is the thing
            // the caller asked for and it is already computed; losing the prose is a degradation,
            // not an error.
            _logger.LogWarning(ex, "Narration failed for sprint {SprintId}", input.Report.Summary.SprintId);

            return null;
        }
    }

    /// <summary>The shape the model must answer in.</summary>
    private static JsonOutputFormat Schema() => new()
    {
        Schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                headline = new { type = "string" },
                summary = new { type = "string" },
                outcome = new { type = "string" },
                observations = new
                {
                    type = "array",
                    items = new { type = "string" },
                },
                shipped = new
                {
                    type = "array",
                    items = new { type = "string" },
                },
                didNotLand = new
                {
                    type = "array",
                    items = new { type = "string" },
                },
                whereWorkIsSitting = new
                {
                    type = "array",
                    items = new { type = "string" },
                },
            }),
            ["required"] = JsonSerializer.SerializeToElement(
                new[]
                {
                    "headline", "summary", "outcome",
                    "observations", "shipped", "didNotLand", "whereWorkIsSitting",
                }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        },
    };

    private sealed record NarrativeShape(
        string? Headline,
        string? Summary,
        string? Outcome,
        List<string>? Observations,
        List<string>? Shipped,
        List<string>? DidNotLand,
        List<string>? WhereWorkIsSitting);
}
