namespace BoardSync.Api.Modules.Intelligence.Providers;

/// <summary>
/// Builds a Gemini client, or nothing when none is configured.
/// </summary>
/// <remarks>
/// The null is the point. Both adapters answer <c>IsConfigured = false</c> and return no result
/// rather than throwing, so a deployment that wants no model sees narratives and decompositions
/// politely unavailable instead of errors on every request — the same shape the other provider has.
/// </remarks>
public sealed class GeminiClientFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly string _model;

    /// <summary>The named client, so the base address and timeout are configured in one place.</summary>
    public const string HttpClientName = "gemini";

    public GeminiClientFactory(
        IHttpClientFactory httpFactory,
        IConfiguration configuration)
    {
        _httpFactory = httpFactory;

        _apiKey = configuration["Intelligence:GeminiApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        /*
         * Configurable because model names move faster than releases do. A deployment that needs a
         * different one should not need a build.
         */
        var model = configuration["Intelligence:GeminiModel"]
            ?? Environment.GetEnvironmentVariable("GEMINI_MODEL");

        // An empty value in a `.env` reads as "not set", not as a model called "".
        _model = string.IsNullOrWhiteSpace(model)
            ? "gemini-2.5-flash"
            : model.Trim();
    }

    public GeminiClient? Create() =>
        string.IsNullOrWhiteSpace(_apiKey)
            ? null
            : new GeminiClient(
                _httpFactory.CreateClient(HttpClientName),
                _apiKey,
                _model);
}
