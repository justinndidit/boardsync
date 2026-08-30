using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Intelligence.Providers;

/// <summary>
/// The slice of the Gemini API this product uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain HTTP rather than an SDK.</b> What is needed here is one endpoint —
/// <c>generateContent</c> with a response schema — and taking a dependency to reach it would mean
/// adopting a package's release cadence and breaking changes for the sake of a POST. The request
/// and response shapes below are the whole surface.
/// </para>
/// <para>
/// Structured output is asked for the same way it is of the other provider: a JSON schema the model
/// must answer in. Gemini's schema dialect is a subset of OpenAPI and differs from JSON Schema in
/// two ways that matter — <c>additionalProperties</c> is not supported, and property order is
/// carried by <c>propertyOrdering</c> rather than implied. Both are handled where the schemas are
/// built, not here.
/// </para>
/// </remarks>
public sealed class GeminiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public GeminiClient(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>
    /// Asks for one structured answer.
    /// </summary>
    /// <returns>
    /// The model's JSON text and what it cost, or null when the call failed. Null rather than an
    /// exception because both callers treat a missing answer as a degradation to report, not a
    /// fault to propagate.
    /// </returns>
    public async Task<GeminiAnswer?> GenerateAsync(
        string systemPrompt,
        string userContent,
        object responseSchema,
        int maxOutputTokens,
        CancellationToken ct = default)
    {
        /*
         * The key travels as a header rather than a query parameter. Both are accepted; a query
         * string ends up in access logs and proxy traces, which is a poor place for a credential.
         */
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"v1beta/models/{_model}:generateContent");

        request.Headers.Add("x-goog-api-key", _apiKey);

        request.Content = JsonContent.Create(new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } },
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userContent } },
                },
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema,
                maxOutputTokens,

                // Low but not zero. These are structured extractions, not creative writing, and a
                // sprint report narrated differently on every refresh reads as unreliable.
                temperature = 0.2,
            },
        }, options: Json);

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new HttpRequestException(
                $"Gemini returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        var parsed = await response.Content
            .ReadFromJsonAsync<GenerateContentResponse>(Json, ct);

        var text = parsed?.Candidates?
            .FirstOrDefault()?
            .Content?.Parts?
            .Select(part => part.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (string.IsNullOrWhiteSpace(text)) return null;

        /*
         * Total, not prompt plus candidates. Gemini reports thinking tokens separately and folds
         * them into the total, so summing the two visible figures would undercount what was spent —
         * and a budget that undercounts is one somebody can overrun.
         */
        var tokens = parsed?.UsageMetadata?.TotalTokenCount
            ?? ((parsed?.UsageMetadata?.PromptTokenCount ?? 0)
                + (parsed?.UsageMetadata?.CandidatesTokenCount ?? 0));

        return new GeminiAnswer(text, tokens);
    }

    /// <summary>Keeps a failure message readable in a log line.</summary>
    private static string Truncate(string body) =>
        body.Length <= 500 ? body : body[..500] + "…";

    private sealed record GenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<Candidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] Usage? UsageMetadata);

    private sealed record Candidate(
        [property: JsonPropertyName("content")] Content? Content);

    private sealed record Content(
        [property: JsonPropertyName("parts")] List<Part>? Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record Usage(
        [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount,
        [property: JsonPropertyName("totalTokenCount")] int? TotalTokenCount);
}

/// <summary>One structured answer and what it cost.</summary>
public readonly record struct GeminiAnswer(string Text, int TokensSpent);
