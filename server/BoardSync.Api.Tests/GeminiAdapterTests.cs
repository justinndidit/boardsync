using System.Text.Json;

using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.Providers;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoardSync.Api.Tests;

/// <summary>
/// The Gemini adapters, as far as they can be checked without a key.
/// </summary>
/// <remarks>
/// <para>
/// The model call itself is unexercised — that is what the interfaces exist for, and the guards,
/// budget and acceptance rules around it are covered against a fake elsewhere. What is worth pinning
/// here is the part that is easy to get wrong and invisible until a real request fails: the shape
/// asked for, and the behaviour when no key is present.
/// </para>
/// <para>
/// Gemini's schema is an OpenAPI subset, not JSON Schema. <c>additionalProperties</c> is not
/// supported and sending it is rejected, so its absence is asserted rather than assumed.
/// </para>
/// </remarks>
public class GeminiAdapterTests
{
    private static GeminiClientFactory FactoryWith(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(s =>
                    new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new GeminiClientFactory(
            new StubHttpClientFactory(), configuration);
    }

    [Fact]
    public void WithoutAKeyTheAdaptersReportThemselvesUnconfigured()
    {
        var factory = FactoryWith(("Intelligence:GeminiApiKey", null));

        var narrator = new GeminiNarrator(factory, NullLogger<GeminiNarrator>.Instance);
        var decomposer = new GeminiDecomposer(factory, NullLogger<GeminiDecomposer>.Instance);

        /*
         * Unconfigured, not broken. A deployment that wants no model must see the features politely
         * unavailable rather than an exception on every sprint report — the same shape the other
         * provider has, which is what lets one be swapped for the other.
         */
        Assert.False(narrator.IsConfigured);
        Assert.False(decomposer.IsConfigured);
    }

    [Fact]
    public void WithAKeyTheAdaptersAreConfigured()
    {
        var factory = FactoryWith(("Intelligence:GeminiApiKey", "test-key"));

        Assert.True(new GeminiNarrator(
            factory, NullLogger<GeminiNarrator>.Instance).IsConfigured);

        Assert.True(new GeminiDecomposer(
            factory, NullLogger<GeminiDecomposer>.Instance).IsConfigured);
    }

    [Fact]
    public async Task AnUnconfiguredAdapterReturnsNothingRatherThanThrowing()
    {
        var factory = FactoryWith(("Intelligence:GeminiApiKey", null));

        var decomposer = new GeminiDecomposer(
            factory, NullLogger<GeminiDecomposer>.Instance);

        Assert.Null(await decomposer.DecomposeAsync("anything"));
    }

    [Fact]
    public void BothProvidersAskForTheSameThings()
    {
        /*
         * The prompts are shared rather than copied. Two versions of the hierarchy rule would drift,
         * and the drift would surface as one provider quietly producing trees the guard rejects.
         */
        Assert.Contains(
            WorkItemHierarchy.Description,
            IntelligencePrompts.Decomposer);

        Assert.Contains(
            "Every number you write MUST appear in it",
            IntelligencePrompts.Narrator);
    }

    [Fact]
    public void TheDecompositionSchemaIsInGeminisDialect()
    {
        var json = SerializeSchema(typeof(GeminiDecomposer));

        /*
         * Gemini's schema is an OpenAPI subset. `additionalProperties` is not part of it and sending
         * it is rejected outright — a failure that only appears on a real call, which is exactly the
         * kind this test exists to catch without one.
         */
        Assert.DoesNotContain("additionalProperties", json);

        // The enums have to be the domain's, or the guard rejects everything the model returns.
        foreach (var type in Enum.GetNames<WorkItemType>())
            Assert.Contains(type, json);

        Assert.Contains("propertyOrdering", json);
    }

    [Fact]
    public void TheLeafLevelCannotHaveChildren()
    {
        var schema = JsonDocument.Parse(
            SerializeSchema(typeof(GeminiDecomposer))).RootElement;

        var node = schema
            .GetProperty("properties")
            .GetProperty("roots")
            .GetProperty("items");

        /*
         * Walk to the bottom of the unrolled tree. Epic → Feature → Story → Task/Bug is four levels,
         * and the fourth must offer no `children` at all — the one part of the nesting rule a schema
         * can carry, and the only thing stopping a model nesting under a Task.
         */
        var depth = 1;

        while (node.GetProperty("properties").TryGetProperty("children", out var children))
        {
            node = children.GetProperty("items");
            depth++;
        }

        Assert.Equal(4, depth);
    }

    /// <summary>The private schema builder, as JSON — what would actually go over the wire.</summary>
    private static string SerializeSchema(Type adapter)
    {
        var method = adapter.GetMethod(
            "Schema",
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static)!;

        return JsonSerializer.Serialize(
            method.Invoke(null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new()
        {
            BaseAddress = new Uri("https://example.invalid/"),
        };
    }
}
