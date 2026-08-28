using System.Text.Json;

using Anthropic;
using Anthropic.Models.Messages;

using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.WorkItems.Domain;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Structured output against a schema that mirrors the real hierarchy, so acceptance is a direct
/// map — build_context.md §8.2. The nesting is carried by the shape of the response rather than by
/// parent references the model would have to keep consistent.
/// </para>
/// <para>
/// <b>The schema cannot express the nesting rule.</b> JSON Schema recursion permits any type at any
/// depth, so nothing here stops a Task being proposed under an Epic; the system prompt asks for the
/// right shape and <see cref="DecompositionGuard"/> enforces it. That split is deliberate — a
/// prompt is a request, and only the guard is a guarantee.
/// </para>
/// </remarks>
public sealed class ClaudeDecomposer : IDecomposer
{
    private readonly AnthropicClient? _client;
    private readonly ILogger<ClaudeDecomposer> _logger;

    /// <summary>
    /// Room for a full hierarchy over a substantial PRD.
    /// </summary>
    /// <remarks>
    /// Larger than the narrator's by an order of magnitude, because the output here is the artifact
    /// rather than a comment on one. <see cref="DecompositionGuard.MaxNodes"/> is the real limit on
    /// size; this only has to be big enough not to truncate a tree that would pass it.
    /// </remarks>
    private const int MaxTokens = 16_000;

    /// <summary>How deep the schema allows nesting: Epic → Feature → Story → Task/Bug.</summary>
    private const int HierarchyDepth = 4;

    public ClaudeDecomposer(IConfiguration configuration, ILogger<ClaudeDecomposer> logger)
    {
        _logger = logger;

        var apiKey = configuration["Intelligence:AnthropicApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        // Absent rather than broken, exactly as ClaudeNarrator: a deployment with no key sees the
        // feature hidden, never an error.
        _client = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new AnthropicClient { ApiKey = apiKey };
    }

    public bool IsConfigured => _client is not null;

    public async Task<DecompositionOutcome?> DecomposeAsync(
        string document,
        CancellationToken ct = default)
    {
        if (_client is null) return null;

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = "claude-opus-5",
                MaxTokens = MaxTokens,

                // Adaptive thinking, and unlike the narrator this one gets high effort. Reading a
                // requirements document and finding the seams the work actually splits along is the
                // reasoning task here; a cheaper pass returns the document's own headings restated
                // as epics, which is worse than useless because it looks like work was done.
                Thinking = new ThinkingConfigAdaptive(),

                OutputConfig = new OutputConfig
                {
                    Effort = Effort.High,
                    Format = Schema(),
                },

                System = SystemPrompt,

                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = $"Requirements document:\n\n{document}",
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

            var parsed = JsonSerializer.Deserialize<DecompositionShape>(
                text,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (parsed?.Roots is null) return null;

            return new DecompositionOutcome(
                new Decomposition(parsed.Roots, parsed.Notes ?? []),
                (int)((response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0)));
        }
        catch (Exception ex)
        {
            // Recorded on the proposal as a failure reason, not thrown. The requester asked for a
            // suggestion; not getting one is a disappointment, not a server fault.
            _logger.LogWarning(ex, "Decomposition failed");

            return null;
        }
    }

    /// <summary>
    /// The instructions, which are identical for every decomposition.
    /// </summary>
    /// <remarks>
    /// A constant so the prefix is byte-identical across calls. Nothing project-specific goes in
    /// here — the moment it does, every request has a different prefix.
    /// </remarks>
    private static readonly string SystemPrompt = $"""
        You break product requirements documents into a hierarchy of work items for a software team.

        The hierarchy is strict: {WorkItemHierarchy.Description}. An Epic may contain only Features,
        a Feature only User Stories, a User Story only Tasks and Bugs. Tasks and Bugs are leaves and
        contain nothing. A tree that violates this will be rejected in full, so check it before you
        answer.

        You do not have to use every level. A small document may be a few User Stories with Tasks
        under them, and forcing an Epic over the top of it adds a layer nobody wanted.

        Titles are what someone reads on a board: short, specific, and starting with a verb where
        there is one. "Add rate limiting to the login endpoint", not "Rate limiting" and not
        "As a user, I want the system to be protected from abuse so that...". Put the detail in the
        description.

        Only decompose what the document actually says. If a requirement is ambiguous, or implies
        work the document does not describe, put it in `notes` rather than inventing a work item for
        it. A tree that quietly fills gaps is the failure mode here — a reviewer cannot tell which
        parts came from the document and which you supplied.

        Estimate story points only where the document gives you enough to estimate from. Omit the
        field otherwise; an absent estimate reads as "not estimated", and a guessed one reads as a
        judgment nobody made.

        Priority reflects what the document says about urgency, not your own view of what matters.
        Use Medium when it says nothing.
        """;

    /// <summary>
    /// The response schema: a recursive tree, unrolled to the depth the domain allows.
    /// </summary>
    /// <remarks>
    /// Unrolled rather than expressed with a <c>$ref</c> cycle, because the depth is fixed at four
    /// and an explicit shape is one fewer thing that has to behave. The leaf level has no children
    /// property at all, which is the one part of the nesting rule the schema can carry.
    /// </remarks>
    private static JsonOutputFormat Schema()
    {
        var node = NodeSchema(HierarchyDepth);

        return new JsonOutputFormat
        {
            Schema = new Dictionary<string, JsonElement>
            {
                ["type"] = JsonSerializer.SerializeToElement("object"),
                ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["roots"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = node,
                    },
                    ["notes"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                    },
                }),
                ["required"] = JsonSerializer.SerializeToElement(new[] { "roots", "notes" }),
                ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            },
        };
    }

    /// <summary>One level of the tree, with <paramref name="remainingDepth"/> levels beneath it.</summary>
    private static Dictionary<string, object> NodeSchema(int remainingDepth)
    {
        var properties = new Dictionary<string, object>
        {
            ["title"] = new Dictionary<string, object> { ["type"] = "string" },
            ["description"] = new Dictionary<string, object> { ["type"] = "string" },
            ["type"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = Enum.GetNames<WorkItems.Models.WorkItemType>(),
            },
            ["priority"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = Enum.GetNames<WorkItems.Models.WorkItemPriority>(),
            },
            ["storyPoints"] = new Dictionary<string, object> { ["type"] = "integer" },
        };

        if (remainingDepth > 1)
        {
            properties["children"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = NodeSchema(remainingDepth - 1),
            };
        }

        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new[] { "title", "type" },
            ["additionalProperties"] = false,
        };
    }

    private sealed record DecompositionShape(
        List<ProposedNode>? Roots,
        List<string>? Notes);
}
