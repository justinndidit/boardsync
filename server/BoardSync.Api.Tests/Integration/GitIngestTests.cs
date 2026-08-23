using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BoardSync.Api.Data;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That webhook ingest is safe, idempotent, fast, and actually reaches the worker.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint is the only anonymous write surface in the product, and the only one reachable by
/// anybody who learns a URL. Its unit tests cover the signature maths; these cover the parts that
/// only exist once it is wired — that an unverified delivery writes nothing, that a redelivery does
/// not do the work twice, that the request returns before the work happens, and that the job
/// actually runs.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class GitIngestTests(BoardSyncApiFactory factory)
{
    private const string Secret = "integration-webhook-secret";

    /// <summary>Creates a connected installation with one repository wired to a project.</summary>
    private async Task<(Workspace Workspace, string EndpointToken, string RepositoryId)> ConnectAsync()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var endpointToken = InstallationSecrets.NewEndpointToken();
        var repositoryId = Random.Shared.Next(100_000, 999_999).ToString();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        var installation = new GitProviderInstallation
        {
            OrganizationId = workspace.OrganizationId,
            Provider = GitProvider.GitHub,
            ExternalId = $"install-{Guid.NewGuid():N}"[..20],
            AccountName = "acme",
            WebhookSecret = Secret,
            Verification = WebhookVerification.HmacSha256,
            EndpointToken = endpointToken
        };

        context.GitProviderInstallations.Add(installation);
        context.RepositoryLinks.Add(new RepositoryLink
        {
            InstallationId = installation.Id,
            ProjectId = workspace.ProjectId,
            RepositoryExternalId = repositoryId,
            RepositoryName = "acme/payments",
            DefaultBranch = "main"
        });

        await context.SaveChangesAsync();

        return (workspace, endpointToken, repositoryId);
    }

    private static string PushPayload(string repositoryId, string branch = "bs-1-work") => $$"""
    {
      "ref": "refs/heads/{{branch}}",
      "created": false,
      "repository": { "id": {{repositoryId}}, "full_name": "acme/payments" },
      "pusher": { "name": "ada", "email": "ada@acme.test" },
      "commits": [
        { "id": "{{Guid.NewGuid():N}}", "message": "BS-1 some work",
          "author": { "name": "Ada", "email": "ada@acme.test" },
          "timestamp": "2026-08-23T10:00:00Z" }
      ]
    }
    """;

    /// <summary>Posts a delivery, signing it correctly unless told otherwise.</summary>
    private async Task<HttpResponseMessage> DeliverAsync(
        string endpointToken,
        string payload,
        string? deliveryId = null,
        string eventName = "push",
        string? signWith = Secret)
    {
        var http = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(payload);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/git/github/webhook/{endpointToken}")
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", eventName);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", deliveryId ?? Guid.NewGuid().ToString());

        if (signWith is not null)
        {
            var signature = "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(signWith), body));

            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        }

        return await http.SendAsync(request);
    }

    private async Task<int> DeliveryCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
            .WebhookDeliveries.CountAsync();
    }

    // ── Verification ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AGenuineDeliveryIsAccepted()
    {
        var (_, token, repositoryId) = await ConnectAsync();

        var response = await DeliverAsync(token, PushPayload(repositoryId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>
    /// An unsigned or wrongly-signed delivery is refused and writes nothing.
    /// </summary>
    /// <remarks>
    /// The count assertion is the point. Rejecting with a 401 while still recording the payload would
    /// give an anonymous caller a way to write unbounded rows into the database.
    /// </remarks>
    [Fact]
    public async Task AnUnverifiedDeliveryIsRefusedAndStoresNothing()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var before = await DeliveryCountAsync();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DeliverAsync(token, PushPayload(repositoryId), signWith: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DeliverAsync(token, PushPayload(repositoryId), signWith: "wrong-secret")).StatusCode);

        Assert.Equal(before, await DeliveryCountAsync());
    }

    /// <summary>
    /// An unknown endpoint token answers exactly as a bad signature does.
    /// </summary>
    /// <remarks>
    /// Deliberate: if "no such installation" and "wrong secret" were distinguishable, the endpoint
    /// would become an oracle for discovering which webhook URLs are real.
    /// </remarks>
    [Fact]
    public async Task AnUnknownEndpointTokenIsIndistinguishableFromABadSignature()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var payload = PushPayload(repositoryId);

        var unknownInstallation = await DeliverAsync(
            InstallationSecrets.NewEndpointToken(), payload);

        var wrongSecret = await DeliverAsync(token, payload, signWith: "not-the-secret");

        // Compared against each other rather than against a fixed shape: what matters is that a
        // caller cannot tell the two apart, not what the shared answer happens to look like.
        Assert.Equal(HttpStatusCode.Unauthorized, unknownInstallation.StatusCode);
        Assert.Equal(wrongSecret.StatusCode, unknownInstallation.StatusCode);

        // traceId is per-request and differs on any two calls, so it is excluded — it identifies the
        // request, not which of the two refusals happened, and is the same field on both.
        Assert.Equal(
            WithoutTraceId(await wrongSecret.Content.ReadAsStringAsync()),
            WithoutTraceId(await unknownInstallation.Content.ReadAsStringAsync()));
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    /// <summary>
    /// Redelivering the same delivery id is a success that does no extra work.
    /// </summary>
    /// <remarks>
    /// GitHub reuses the original id when a delivery is redelivered, which is exactly what makes it
    /// the right idempotency key. Answering non-2xx here would teach the provider to keep retrying
    /// something already handled.
    /// </remarks>
    [Fact]
    public async Task ARedeliveryIsAcceptedWithoutDuplicating()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var deliveryId = Guid.NewGuid().ToString();
        var payload = PushPayload(repositoryId);

        Assert.Equal(HttpStatusCode.Accepted,
            (await DeliverAsync(token, payload, deliveryId)).StatusCode);

        var again = await DeliverAsync(token, payload, deliveryId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        Assert.Equal(1, await context.WebhookDeliveries
            .CountAsync(d => d.ProviderDeliveryId == deliveryId));
    }

    // ── The request must not do the work ──────────────────────────────────────

    /// <summary>
    /// Ingest returns promptly, because a provider that waits on us starts retrying.
    /// </summary>
    /// <remarks>
    /// The request does one insert and answers 202; normalization and, later, binding happen in a
    /// job. A generous bound — this is asserting the shape of the design, not benchmarking it.
    /// </remarks>
    [Fact]
    public async Task IngestAnswersWithoutProcessingTheDelivery()
    {
        var (_, token, repositoryId) = await ConnectAsync();

        var started = Stopwatch.StartNew();
        var response = await DeliverAsync(token, PushPayload(repositoryId));
        started.Stop();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2),
            $"Ingest took {started.Elapsed.TotalSeconds:F1}s. Work belongs in the job, not the request.");
    }

    // ── The job pipeline ──────────────────────────────────────────────────────

    /// <summary>
    /// The queued job actually runs and records what the delivery amounted to.
    /// </summary>
    /// <remarks>
    /// End to end through the real worker: accepted, claimed under a lease, normalized, marked
    /// processed. Binding is not implemented yet, so the assertion is on the outcome being recorded
    /// rather than on a work item moving — which is the honest thing to check at this stage, and the
    /// line this test will move when binding lands.
    /// </remarks>
    [Fact]
    public async Task TheQueuedJobRunsAndRecordsAnOutcome()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var deliveryId = Guid.NewGuid().ToString();

        await DeliverAsync(token, PushPayload(repositoryId), deliveryId);

        var processed = await PollAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
                .WebhookDeliveries
                .Where(d => d.ProviderDeliveryId == deliveryId)
                .Select(d => d.Outcome)
                .FirstOrDefaultAsync();
        }, TimeSpan.FromSeconds(20));

        Assert.NotNull(processed);
        Assert.Contains("Push on acme/payments", processed);

        // This payload's branch references a key no project in this test owns, so it binds nothing —
        // which is the honest outcome and is recorded rather than left silent. GitDrivenBoardTests
        // covers the case where it does bind.
        Assert.Contains("no work item referenced", processed);
    }

    /// <summary>
    /// A delivery for a repository nobody linked is recorded as ignored, not as a failure.
    /// </summary>
    /// <remarks>
    /// A GitHub App installation covers every repository on the account, so most deliveries will be
    /// for repositories nobody wired to a project. Saying so in the outcome is what separates "the
    /// integration is quiet" from "the integration is broken" — a question that is otherwise
    /// unanswerable.
    /// </remarks>
    [Fact]
    public async Task ADeliveryForAnUnlinkedRepositoryIsRecordedAsIgnored()
    {
        var (_, token, _) = await ConnectAsync();
        var deliveryId = Guid.NewGuid().ToString();

        await DeliverAsync(token, PushPayload("55555555"), deliveryId);

        var outcome = await PollAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
                .WebhookDeliveries
                .Where(d => d.ProviderDeliveryId == deliveryId)
                .Select(d => d.Outcome)
                .FirstOrDefaultAsync();
        }, TimeSpan.FromSeconds(20));

        Assert.NotNull(outcome);
        Assert.Contains("not linked", outcome);
    }

    /// <summary>An event type nobody handles is recorded as ignored rather than retried.</summary>
    [Fact]
    public async Task AnUnhandledEventTypeIsRecordedAsIgnored()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var deliveryId = Guid.NewGuid().ToString();

        var response = await DeliverAsync(
            token,
            $$"""{ "repository": { "id": {{repositoryId}}, "full_name": "acme/payments" }, "zen": "x" }""",
            deliveryId,
            eventName: "ping");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var outcome = await PollAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
                .WebhookDeliveries
                .Where(d => d.ProviderDeliveryId == deliveryId)
                .Select(d => d.Outcome)
                .FirstOrDefaultAsync();
        }, TimeSpan.FromSeconds(20));

        Assert.NotNull(outcome);
        Assert.Contains("no rule for 'ping'", outcome);
    }

    /// <summary>
    /// The raw payload is kept, so a binding bug can be fixed and replayed.
    /// </summary>
    [Fact]
    public async Task TheRawPayloadIsRetainedForReplay()
    {
        var (_, token, repositoryId) = await ConnectAsync();
        var deliveryId = Guid.NewGuid().ToString();
        var payload = PushPayload(repositoryId);

        await DeliverAsync(token, payload, deliveryId);

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
            .WebhookDeliveries
            .Where(d => d.ProviderDeliveryId == deliveryId)
            .Select(d => new { d.Payload, d.Verification, d.EventName })
            .FirstAsync();

        Assert.Contains("bs-1-work", stored.Payload);
        Assert.Equal("push", stored.EventName);

        // What this delivery was trusted on, recorded per row — it is not the same across providers.
        Assert.Equal(WebhookVerification.HmacSha256, stored.Verification);
    }

    private static string WithoutTraceId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"…\"");

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> read, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            var value = await read();
            if (value is not null) return value;
            await Task.Delay(100);
        }

        return default;
    }
}
