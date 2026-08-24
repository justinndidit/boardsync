using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a GitLab or Azure DevOps connection works end to end, not just as an adapter in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProviderConformanceTests</c> proves the three adapters answer the same questions the same way.
/// This proves the rest of the path is genuinely provider-agnostic: the same connect endpoint, the
/// same webhook route, the same idempotency, the same job.
/// </para>
/// <para>
/// It also asserts the part that is <b>not</b> uniform — what an administrator is told about how
/// strongly their deliveries are verified — because that differs by provider and the difference is
/// the customer's to weigh.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class MultiProviderIngestTests(BoardSyncApiFactory factory)
{
    private sealed record Secrets(
        Guid Id, string WebhookUrl, string WebhookSecret, string Verification, string Guidance);

    private async Task<(Workspace Workspace, Secrets Secrets)> ConnectAsync(string provider)
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations",
            new
            {
                provider,
                externalId = $"inst-{Guid.NewGuid():N}"[..20],
                accountName = "acme"
            });

        return (workspace, secrets);
    }

    private async Task<HttpResponseMessage> DeliverAsync(
        Secrets secrets, string payload, Action<HttpRequestMessage> authenticate, string? eventHeader = null)
    {
        var http = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(payload);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(secrets.WebhookUrl).AbsolutePath)
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (eventHeader is not null)
            request.Headers.TryAddWithoutValidation("X-Gitlab-Event", eventHeader);

        authenticate(request);

        return await http.SendAsync(request);
    }

    // ── GitLab ────────────────────────────────────────────────────────────────

    private static string GitLabPush(string repositoryId) => $$"""
    {
      "object_kind": "push",
      "before": "9d887a1",
      "ref": "refs/heads/bs-1-work",
      "user_username": "ada",
      "project": { "id": {{repositoryId}}, "path_with_namespace": "acme/payments" },
      "commits": [
        { "id": "{{Guid.NewGuid():N}}", "message": "BS-1 work",
          "author": { "name": "Ada", "email": "ada@acme.test" },
          "timestamp": "2026-08-24T10:00:00Z" }
      ]
    }
    """;

    [Fact]
    public async Task AGitLabDeliveryIsAcceptedAndProcessed()
    {
        var (workspace, secrets) = await ConnectAsync("GitLab");

        var accepted = await DeliverAsync(
            secrets,
            GitLabPush("777001"),
            r => r.Headers.TryAddWithoutValidation("X-Gitlab-Token", secrets.WebhookSecret),
            eventHeader: "Push Hook");

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var outcome = await PollOutcomeAsync(workspace, secrets.Id);

        Assert.NotNull(outcome);
        Assert.Contains("acme/payments", outcome);
    }

    [Fact]
    public async Task AGitLabDeliveryWithTheWrongTokenIsRefused()
    {
        var (_, secrets) = await ConnectAsync("GitLab");

        var refused = await DeliverAsync(
            secrets,
            GitLabPush("777002"),
            r => r.Headers.TryAddWithoutValidation("X-Gitlab-Token", "not-the-secret"),
            eventHeader: "Push Hook");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// GitLab sends no delivery id, so idempotency falls back to the payload digest.
    /// </summary>
    /// <remarks>
    /// Weaker than a provider-assigned id — two genuinely identical events would collapse into one —
    /// and it is the only idempotency available for a provider that does not supply one. Worth a test
    /// because it is a real behavioural difference from GitHub, not a detail.
    /// </remarks>
    [Fact]
    public async Task AGitLabRedeliveryOfTheSameBytesDeduplicates()
    {
        var (_, secrets) = await ConnectAsync("GitLab");
        var payload = GitLabPush("777003");

        void Authenticate(HttpRequestMessage r) =>
            r.Headers.TryAddWithoutValidation("X-Gitlab-Token", secrets.WebhookSecret);

        Assert.Equal(HttpStatusCode.Accepted,
            (await DeliverAsync(secrets, payload, Authenticate, "Push Hook")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await DeliverAsync(secrets, payload, Authenticate, "Push Hook")).StatusCode);
    }

    // ── Azure DevOps ──────────────────────────────────────────────────────────

    private static string AzurePush(string repositoryId) => $$"""
    {
      "eventType": "git.push",
      "resource": {
        "repository": { "id": "{{repositoryId}}", "name": "acme/payments",
                        "defaultBranch": "refs/heads/main" },
        "refUpdates": [ { "name": "refs/heads/bs-1-work" } ],
        "pushedBy": { "displayName": "Ada", "uniqueName": "ada@acme.test" },
        "commits": [
          { "commitId": "{{Guid.NewGuid():N}}", "comment": "BS-1 work",
            "author": { "name": "Ada", "email": "ada@acme.test",
                        "date": "2026-08-24T10:00:00Z" } }
        ]
      }
    }
    """;

    private static void BasicAuth(HttpRequestMessage request, string password) =>
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"boardsync:{password}")));

    [Fact]
    public async Task AnAzureDevOpsDeliveryIsAcceptedAndProcessed()
    {
        var (workspace, secrets) = await ConnectAsync("AzureDevOps");

        var accepted = await DeliverAsync(
            secrets,
            AzurePush(Guid.NewGuid().ToString()),
            r => BasicAuth(r, secrets.WebhookSecret));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var outcome = await PollOutcomeAsync(workspace, secrets.Id);

        Assert.NotNull(outcome);
        Assert.Contains("acme/payments", outcome);
    }

    [Fact]
    public async Task AnAzureDevOpsDeliveryWithTheWrongPasswordIsRefused()
    {
        var (_, secrets) = await ConnectAsync("AzureDevOps");

        var refused = await DeliverAsync(
            secrets,
            AzurePush(Guid.NewGuid().ToString()),
            r => BasicAuth(r, "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // ── What administrators are told ──────────────────────────────────────────

    /// <summary>
    /// Each provider's connect response records and explains the verification it actually offers.
    /// </summary>
    /// <remarks>
    /// The asymmetry is real and is the customer's to weigh: GitHub proves origin and contents,
    /// GitLab and Azure DevOps prove only that the caller knew a secret. Saying so plainly at the
    /// moment somebody connects builds trust rather than spending it — and hiding it would make the
    /// per-delivery verification record a detail nobody could interpret.
    /// </remarks>
    [Theory]
    [InlineData("GitHub", "HmacSha256", "HMAC-SHA256")]
    [InlineData("GitLab", "SharedSecret", "not that the payload was unaltered")]
    [InlineData("AzureDevOps", "BasicAuth", "cannot sign payloads")]
    public async Task ConnectingExplainsWhatThisProvidersVerificationProves(
        string provider, string expectedVerification, string expectedGuidance)
    {
        var (_, secrets) = await ConnectAsync(provider);

        Assert.Equal(expectedVerification, secrets.Verification);
        Assert.Contains(expectedGuidance, secrets.Guidance);
    }

    /// <summary>Each provider gets its own webhook route, keyed on its own name.</summary>
    [Theory]
    [InlineData("GitHub", "/api/git/github/webhook/")]
    [InlineData("GitLab", "/api/git/gitlab/webhook/")]
    [InlineData("AzureDevOps", "/api/git/azuredevops/webhook/")]
    public async Task EachProviderGetsItsOwnWebhookUrl(string provider, string expectedPath)
    {
        var (_, secrets) = await ConnectAsync(provider);

        Assert.Contains(expectedPath, secrets.WebhookUrl);
    }

    private async Task<string?> PollOutcomeAsync(Workspace workspace, Guid installationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var page = await workspace.Owner.Get<Paged<Delivery>>(
                $"/api/git/installations/{installationId}/deliveries");

            var processed = page.Items.FirstOrDefault(d => d.Outcome is not null);

            if (processed is not null) return processed.Outcome;

            await Task.Delay(100);
        }

        return null;
    }

    private sealed record Delivery(Guid Id, string EventName, string? Outcome);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
