using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That connecting a git host is self-service, and that the boundaries around it hold.
/// </summary>
/// <remarks>
/// Two authorities meet here. A connection is an organization-wide credential reaching every
/// repository on the account, so it is <c>org:admin</c>. A repository link decides which board a
/// commit can move, so it is <c>project:admin</c> — a project administrator can wire up their own
/// project without being handed the organization's git account.
/// </remarks>
[Collection(ApiCollection.Name)]
public class GitConnectionTests(BoardSyncApiFactory factory)
{
    private static object ConnectBody(string? account = null) => new
    {
        provider = "GitHub",
        externalId = $"inst-{Guid.NewGuid():N}"[..20],
        accountName = account ?? "acme"
    };

    private static object LinkBody(Guid installationId, string? repositoryId = null) => new
    {
        installationId,
        repositoryExternalId = repositoryId ?? Random.Shared.Next(100_000, 999_999).ToString(),
        repositoryName = "acme/payments",
        defaultBranch = "main"
    };

    // ── Connecting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connecting returns the webhook URL and secret, once.
    /// </summary>
    /// <remarks>
    /// The whole point of the endpoint: an administrator needs both to configure the hook at the
    /// provider, and nothing else in the API will ever hand them the secret again.
    /// </remarks>
    [Fact]
    public async Task ConnectingReturnsTheWebhookUrlAndSecret()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        Assert.Contains("/api/git/github/webhook/", secrets.WebhookUrl);
        Assert.False(string.IsNullOrWhiteSpace(secrets.WebhookSecret));
        Assert.Equal("HmacSha256", secrets.Verification);

        // The guidance says what this provider's verification actually proves. It differs across
        // providers and the difference is the customer's to weigh.
        Assert.Contains("HMAC-SHA256", secrets.Guidance);
    }

    /// <summary>
    /// The secret never appears again, on any endpoint.
    /// </summary>
    /// <remarks>
    /// A credential that can be read back turns every future read-access bug into a credential leak.
    /// Rotating is the recovery path, not retrieval.
    /// </remarks>
    [Fact]
    public async Task TheSecretIsNeverReturnedAgain()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var listed = await workspace.Owner.GetRaw($"/api/orgs/{workspace.OrganizationId}/git/installations");
        var body = await listed.Content.ReadAsStringAsync();

        Assert.DoesNotContain(secrets.WebhookSecret, body);

        // Nor the endpoint token, which for a provider that cannot sign payloads is most of the
        // security there is.
        var token = secrets.WebhookUrl[(secrets.WebhookUrl.LastIndexOf('/') + 1)..];
        Assert.DoesNotContain(token, body);
    }

    /// <summary>The same account cannot be connected twice.</summary>
    [Fact]
    public async Task ConnectingTheSameAccountTwiceConflicts()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var body = ConnectBody();

        await workspace.Owner.Post<Secrets>($"/api/orgs/{workspace.OrganizationId}/git/installations", body);

        var again = await workspace.Owner.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", body);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>Connecting a git host is not something an ordinary member may do.</summary>
    [Fact]
    public async Task ConnectingRequiresOrganizationAdministration()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var member = await workspace.AddOrganizationMemberAsync(factory);

        var response = await member.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Rotating issues a different secret and keeps the same URL.</summary>
    /// <remarks>
    /// The URL identifies the installation and does not change, so the provider's configuration needs
    /// only its secret updated — which is why the response repeats the URL rather than only the key.
    /// </remarks>
    [Fact]
    public async Task RotatingIssuesANewSecretForTheSameUrl()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var first = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var rotated = await workspace.Owner.Post<Secrets>(
            $"/api/git/installations/{first.Id}/rotate-secret", new { });

        Assert.Equal(first.WebhookUrl, rotated.WebhookUrl);
        Assert.NotEqual(first.WebhookSecret, rotated.WebhookSecret);
    }

    /// <summary>
    /// After rotating, deliveries signed with the old secret are refused.
    /// </summary>
    /// <remarks>
    /// The reason rotation is the recovery path for a leaked secret: it has to actually invalidate
    /// the old one, immediately.
    /// </remarks>
    [Fact]
    public async Task DeliveriesSignedWithTheOldSecretStopWorkingAfterRotation()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var first = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var url = new Uri(first.WebhookUrl).AbsolutePath;

        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(url, first.WebhookSecret)).StatusCode);

        await workspace.Owner.Post<Secrets>($"/api/git/installations/{first.Id}/rotate-secret", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(url, first.WebhookSecret)).StatusCode);
    }

    // ── Linking ───────────────────────────────────────────────────────────────

    /// <summary>Linking a repository grants the installation its role on the project.</summary>
    /// <remarks>
    /// The grant is what makes the whole thing work, and what bounds it: contribution without
    /// certification. Asserted through the capabilities endpoint, which reports exactly what the
    /// guards will allow.
    /// </remarks>
    [Fact]
    public async Task LinkingGrantsTheInstallationContributionWithoutCertification()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        await workspace.Owner.Post<Link>(
            $"/api/projects/{workspace.ProjectId}/git/repositories", LinkBody(secrets.Id));

        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .CreateScope(factory.Services);

        var rbac = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Modules.Rbac.Services.Interfaces.IRbacService>(scope.ServiceProvider);

        var held = await rbac.GetPermissionsAtAsync(
            secrets.Id,
            new Modules.Rbac.Models.ScopeRef(
                Modules.Rbac.Models.RoleScope.Project, workspace.ProjectId));

        Assert.Contains(Modules.Rbac.Models.Permissions.WorkItemWrite, held);
        Assert.DoesNotContain(Modules.Rbac.Models.Permissions.WorkItemVerify, held);
    }

    /// <summary>Linked repositories are visible to anyone who can see the project.</summary>
    /// <remarks>
    /// Knowing which repository moves your board is part of understanding the board, not an
    /// administrative detail — so this is <c>project:read</c>, not <c>project:admin</c>.
    /// </remarks>
    [Fact]
    public async Task LinkedRepositoriesAreVisibleToProjectReaders()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var reader = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = reader.UserId, role = "Viewer" });

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        await workspace.Owner.Post<Link>(
            $"/api/projects/{workspace.ProjectId}/git/repositories", LinkBody(secrets.Id));

        var links = await reader.Get<List<Link>>($"/api/projects/{workspace.ProjectId}/git/repositories");

        Assert.Single(links);
        Assert.Equal("acme/payments", links[0].RepositoryName);
    }

    /// <summary>
    /// A repository cannot be wired to a project in another organization.
    /// </summary>
    /// <remarks>
    /// The boundary that matters most here. Without it, a project administrator could link another
    /// organization's repository to their own project and receive its commit messages and branch
    /// names through the delivery history — a data leak wearing a configuration screen.
    /// </remarks>
    [Fact]
    public async Task ARepositoryCannotBeLinkedAcrossOrganizations()
    {
        var mine = await Workspace.CreateAsync(factory);
        var theirs = await Workspace.CreateAsync(factory);

        var theirInstallation = await theirs.Owner.Post<Secrets>(
            $"/api/orgs/{theirs.OrganizationId}/git/installations", ConnectBody("their-account"));

        var response = await mine.Owner.PostRaw(
            $"/api/projects/{mine.ProjectId}/git/repositories", LinkBody(theirInstallation.Id));

        // 404, not 403: the installation exists but is none of their business, and the two must be
        // indistinguishable from outside.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Linking needs project administration, not merely contribution.</summary>
    [Fact]
    public async Task LinkingRequiresProjectAdministration()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var contributor = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = contributor.UserId, role = "Contributor" });

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var response = await contributor.PostRaw(
            $"/api/projects/{workspace.ProjectId}/git/repositories", LinkBody(secrets.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Disconnecting ─────────────────────────────────────────────────────────

    /// <summary>
    /// Disconnecting stops deliveries being accepted at all.
    /// </summary>
    [Fact]
    public async Task DisconnectingStopsDeliveries()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var url = new Uri(secrets.WebhookUrl).AbsolutePath;

        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync(url, secrets.WebhookSecret)).StatusCode);

        var disconnect = await workspace.Owner.DeleteRaw($"/api/git/installations/{secrets.Id}");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        // A correctly signed delivery for a disconnected installation is refused, so revoking the
        // connection actually stops it rather than merely hiding it from the settings screen.
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(url, secrets.WebhookSecret)).StatusCode);
    }

    // ── Delivery history ──────────────────────────────────────────────────────

    /// <summary>
    /// Deliveries are listed with what each one did.
    /// </summary>
    /// <remarks>
    /// The answer to "is the integration working?". A quiet integration and a broken one are
    /// otherwise identical from the board's point of view.
    /// </remarks>
    [Fact]
    public async Task DeliveriesAreListedWithTheirOutcomes()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var secrets = await workspace.Owner.Post<Secrets>(
            $"/api/orgs/{workspace.OrganizationId}/git/installations", ConnectBody());

        var url = new Uri(secrets.WebhookUrl).AbsolutePath;
        await DeliverAsync(url, secrets.WebhookSecret);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        Paged<Delivery> page;

        do
        {
            page = await workspace.Owner.Get<Paged<Delivery>>(
                $"/api/git/installations/{secrets.Id}/deliveries");

            if (page.Items.Any(d => d.Outcome is not null)) break;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.NotEmpty(page.Items);
        Assert.Equal("push", page.Items[0].EventName);
        Assert.Equal("HmacSha256", page.Items[0].Verification);
        Assert.NotNull(page.Items[0].Outcome);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> DeliverAsync(string path, string secret)
    {
        var http = factory.CreateClient();

        var payload = $$"""
        {
          "ref": "refs/heads/some-branch",
          "created": false,
          "repository": { "id": 424242, "full_name": "acme/payments" },
          "pusher": { "name": "ada" },
          "commits": []
        }
        """;

        var body = Encoding.UTF8.GetBytes(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "push");
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation(
            "X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));

        return await http.SendAsync(request);
    }

    private sealed record Secrets(
        Guid Id, string WebhookUrl, string WebhookSecret, string Verification, string Guidance);

    private sealed record Link(
        Guid Id, Guid InstallationId, string RepositoryExternalId, string RepositoryName, string DefaultBranch);

    private sealed record Delivery(
        Guid Id, string EventName, string Verification, DateTime ReceivedAt,
        DateTime? ProcessedAt, string? Outcome);

    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
