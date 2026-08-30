using System.Text.Json;

using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Intelligence.Providers;

/// <summary>
/// Breaks a requirements document into a proposed hierarchy using Gemini.
/// </summary>
/// <remarks>
/// <para>
/// Same instructions as the other provider, from <see cref="IntelligencePrompts"/>, and the same
/// unrolled four-level schema — the depth is fixed by the domain, so an explicit shape is one fewer
/// thing that has to behave than a recursive reference.
/// </para>
/// <para>
/// <b>The nesting rule still lives in the guard, not here.</b> A schema constrains shape and has no
/// opinion about whether a Task may sit under an Epic; the prompt asks and
/// <c>DecompositionGuard</c> enforces, identically for both providers. That is what makes swapping
/// one for the other a configuration change rather than a change of behaviour.
/// </para>
/// </remarks>
public class GeminiDecomposer : IDecomposer
{
    private readonly GeminiClient? _client;
    private readonly ILogger<GeminiDecomposer> _logger;

    /// <summary>Room for a large tree. The guard caps what a human is asked to review at 150 nodes.</summary>
    private const int MaxTokens = 16_000;

    /// <summary>Epic → Feature → Story → Task/Bug.</summary>
    private const int HierarchyDepth = 4;

    public GeminiDecomposer(
        GeminiClientFactory factory,
        ILogger<GeminiDecomposer> logger)
    {
        _logger = logger;
        _client = factory.Create();
    }

    public bool IsConfigured => _client is not null;

    public async Task<DecompositionOutcome?> DecomposeAsync(
        string document,
        CancellationToken ct = default)
    {
        if (_client is null) return null;

        try
        {
            var answer = await _client.GenerateAsync(
                IntelligencePrompts.Decomposer,
                $"Requirements document:\n\n{document}",
                Schema(),
                MaxTokens,
                ct);

            if (answer is not { } produced) return null;

            var parsed = JsonSerializer.Deserialize<DecompositionShape>(
                produced.Text,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (parsed?.Roots is null) return null;

            return new DecompositionOutcome(
                new Decomposition(parsed.Roots, parsed.Notes ?? [], parsed.Phases ?? []),
                produced.TokensSpent);
        }
        catch (Exception ex)
        {
            // Recorded on the proposal as a failure reason rather than thrown. The requester asked
            // for a suggestion; not getting one is a disappointment, not a server fault.
            _logger.LogWarning(ex, "Decomposition failed");

            return null;
        }
    }

    private static object Schema() => new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["roots"] = new Dictionary<string, object>
            {
                ["type"] = "ARRAY",
                ["items"] = NodeSchema(HierarchyDepth),
            },
            ["notes"] = new Dictionary<string, object>
            {
                ["type"] = "ARRAY",
                ["items"] = new Dictionary<string, object> { ["type"] = "STRING" },
            },
            ["phases"] = new Dictionary<string, object>
            {
                ["type"] = "ARRAY",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["name"] = new Dictionary<string, object> { ["type"] = "STRING" },
                        ["rationale"] = new Dictionary<string, object> { ["type"] = "STRING" },
                    },
                    ["required"] = new[] { "name" },
                    ["propertyOrdering"] = new[] { "name", "rationale" },
                },
            },
        },
        required = new[] { "roots", "notes", "phases" },
        propertyOrdering = new[] { "roots", "notes", "phases" },
    };

    /// <summary>One level of the tree, with <paramref name="remainingDepth"/> levels beneath it.</summary>
    /// <remarks>
    /// The leaf level has no <c>children</c> property at all, which is the one part of the nesting
    /// rule a schema can carry. Everything else about the hierarchy is the guard's job.
    /// </remarks>
    private static Dictionary<string, object> NodeSchema(int remainingDepth)
    {
        var properties = new Dictionary<string, object>
        {
            ["title"] = new Dictionary<string, object> { ["type"] = "STRING" },
            ["description"] = new Dictionary<string, object> { ["type"] = "STRING" },
            ["type"] = new Dictionary<string, object>
            {
                ["type"] = "STRING",
                ["enum"] = Enum.GetNames<WorkItemType>(),
            },
            ["priority"] = new Dictionary<string, object>
            {
                ["type"] = "STRING",
                ["enum"] = Enum.GetNames<WorkItemPriority>(),
            },
            ["storyPoints"] = new Dictionary<string, object> { ["type"] = "INTEGER" },
            ["phase"] = new Dictionary<string, object> { ["type"] = "INTEGER" },
        };

        var order = new List<string>
        {
            "title", "description", "type", "priority", "storyPoints", "phase",
        };

        if (remainingDepth > 1)
        {
            properties["children"] = new Dictionary<string, object>
            {
                ["type"] = "ARRAY",
                ["items"] = NodeSchema(remainingDepth - 1),
            };

            order.Add("children");
        }

        return new Dictionary<string, object>
        {
            ["type"] = "OBJECT",
            ["properties"] = properties,

            /*
             * Only `title` and `type` are required. An estimate is deliberately optional — the
             * prompt says to omit it rather than guess, and requiring it here would force the model
             * to invent one for every node.
             */
            ["required"] = new[] { "title", "type" },
            ["propertyOrdering"] = order.ToArray(),
        };
    }

    private sealed record DecompositionShape(
        List<ProposedNode>? Roots,
        List<string>? Notes,
        List<ProposedPhase>? Phases);
}
