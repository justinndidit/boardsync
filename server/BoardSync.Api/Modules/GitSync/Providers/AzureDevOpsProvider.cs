using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardSync.Api.Modules.GitSync.Models;

namespace BoardSync.Api.Modules.GitSync.Providers;

/// <summary>
/// Azure DevOps, via Service Hooks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Azure DevOps cannot sign a webhook payload.</b> Service Hooks offer HTTP Basic auth and custom
/// headers over HTTPS and nothing else — there is no HMAC, no signature header, no way for a
/// receiver to prove the body arrived unaltered. Anyone who obtains the URL and the credential can
/// post an arbitrary payload.
/// </para>
/// <para>
/// That is not a gap in this adapter; it is the ceiling the provider offers, and it is why the whole
/// ingest path records <see cref="WebhookVerification"/> per delivery instead of assuming every
/// provider is as strong as GitHub. Three things compensate:
/// </para>
/// <list type="number">
///   <item><description>
///     The webhook URL carries a high-entropy per-installation segment, so an attacker who has never
///     seen a real delivery cannot even address an installation. For this provider that token is most
///     of the security, which is why it is generated from a CSPRNG and never returned twice.
///   </description></item>
///   <item><description>
///     The Basic credential is per installation, so one customer's leak cannot forge another's.
///   </description></item>
///   <item><description>
///     A merge reaching <c>Resolved</c> is the highest-consequence transition, and Azure DevOps is
///     the provider least able to prove one happened. Corroborating it by reading the pull request
///     back from the REST API before acting is the remaining control, and it needs the outbound
///     provider clients that Phase C's backfill also needs — see build_context.md §7.3.
///   </description></item>
/// </list>
/// </remarks>
public sealed class AzureDevOpsProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.AzureDevOps;

    public WebhookVerification Verification => WebhookVerification.BasicAuth;

    /// <remarks>
    /// Service Hooks send no event-name header — the payload carries <c>eventType</c> instead. The
    /// ingest path needs a name before it parses anything, so this reports the scheme it can see and
    /// <see cref="TryNormalize"/> reads the real one.
    /// </remarks>
    public string? EventNameOf(IHeaderDictionary headers) => "azuredevops";

    /// <remarks>
    /// No delivery id header. <c>WebhookIngestService</c> derives one from the payload digest, which
    /// is weaker than a provider-assigned id — two genuinely identical events would collapse into
    /// one — and is the only idempotency available here.
    /// </remarks>
    public string? DeliveryIdOf(IHeaderDictionary headers) => null;

    /// <summary>
    /// Checks the Basic credential, in constant time.
    /// </summary>
    /// <remarks>
    /// The password is compared against the installation's secret; the username is ignored, because
    /// Azure DevOps offers no way to bind one and treating it as part of the credential would only
    /// invite a configuration mistake that silently weakens nothing.
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> rawBody, IHeaderDictionary headers, string secret)
    {
        if (!headers.TryGetValue("Authorization", out var header)) return false;

        if (!AuthenticationHeaderValue.TryParse(header.ToString(), out var parsed)
            || !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || parsed.Parameter is not { Length: > 0 } encoded)
        {
            return false;
        }

        string decoded;

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            // Reachable by anyone who knows the URL, so malformed input is a rejection rather than
            // an exception — a 500 tells the sender more than a flat refusal does.
            return false;
        }

        var separator = decoded.IndexOf(':');
        var password = separator >= 0 ? decoded[(separator + 1)..] : decoded;

        // Digested before comparing so a length difference does not short-circuit and leak the
        // secret's length.
        Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(password), presented);
        SHA256.HashData(Encoding.UTF8.GetBytes(secret), expected);

        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    public bool TryNormalize(string eventName, string rawBody, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("resource", out var resource)) return false;
        if (!resource.TryGetProperty("repository", out var repository)) return false;

        var repositoryId = Text(repository, "id") ?? "";
        var repositoryName = Text(repository, "name") ?? "";

        if (repositoryId.Length == 0) return false;

        return Text(root, "eventType") switch
        {
            "git.push" => TryNormalizePush(resource, repositoryId, repositoryName, out normalized),

            "git.pullrequest.created" or "git.pullrequest.updated" or "git.pullrequest.merged" =>
                TryNormalizePullRequest(root, resource, repositoryId, repositoryName, out normalized),

            _ => false
        };
    }

    private static bool TryNormalizePush(
        JsonElement resource, string repositoryId, string repositoryName, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        if (!resource.TryGetProperty("refUpdates", out var refUpdates)
            || refUpdates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var reference = refUpdates.EnumerateArray()
            .Select(r => Text(r, "name"))
            .FirstOrDefault(name => name?.StartsWith("refs/heads/", StringComparison.Ordinal) == true);

        if (reference is null) return false;

        var commits = resource.TryGetProperty("commits", out var array)
            ? array.EnumerateArray().Select(ReadCommit).ToList()
            : [];

        normalized = new NormalizedGitEvent(
            Kind: commits.Count == 0 ? GitEventKind.BranchCreated : GitEventKind.Push,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: reference["refs/heads/".Length..],
            TargetBranch: null,
            Commits: commits,
            PullRequest: null,
            Actor: ReadActor(resource, "pushedBy"),
            OccurredAt: commits.Count > 0 ? commits[^1].CommittedAt : DateTimeOffset.UtcNow);

        return true;
    }

    /// <remarks>
    /// <para>
    /// <b><c>git.pullrequest.merged</c> does not mean the pull request was completed.</b> Azure
    /// DevOps raises it on a merge <em>attempt</em> — including the speculative merge it performs to
    /// check for conflicts — so acting on the event name alone would resolve work items the moment
    /// somebody opened a pull request.
    /// </para>
    /// <para>
    /// <c>status</c> is the field that actually says the pull request completed, so a merge is only
    /// reported when it says so. This is exactly the kind of provider-specific trap the conformance
    /// suite exists to make visible: the same scenario against GitHub turns on a boolean, and against
    /// GitLab on the action name.
    /// </para>
    /// </remarks>
    private static bool TryNormalizePullRequest(
        JsonElement root,
        JsonElement resource,
        string repositoryId,
        string repositoryName,
        out NormalizedGitEvent normalized)
    {
        normalized = null!;

        var eventType = Text(root, "eventType");
        var status = Text(resource, "status");

        var kind = (eventType, status) switch
        {
            ("git.pullrequest.merged", "completed") => GitEventKind.PullRequestMerged,

            // A merge attempt on a pull request that is still open: the conflict check, not a merge.
            ("git.pullrequest.merged", _) => (GitEventKind?)null,

            (_, "abandoned") => GitEventKind.PullRequestClosed,
            ("git.pullrequest.created", _) => GitEventKind.PullRequestOpened,

            // An update that completed the pull request — completion can arrive under this name too.
            ("git.pullrequest.updated", "completed") => GitEventKind.PullRequestMerged,

            _ => null
        };

        if (kind is not { } eventKind) return false;

        normalized = new NormalizedGitEvent(
            Kind: eventKind,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: StripRef(Text(resource, "sourceRefName")),
            TargetBranch: StripRef(Text(resource, "targetRefName")),
            Commits: [],
            PullRequest: new PullRequestInfo(
                Number: resource.TryGetProperty("pullRequestId", out var id) && id.TryGetInt32(out var number)
                    ? number
                    : 0,
                Title: Text(resource, "title") ?? "",
                Body: Text(resource, "description"),
                Url: Text(resource, "url") ?? "",
                Merged: eventKind == GitEventKind.PullRequestMerged),
            Actor: ReadActor(resource, "createdBy"),
            OccurredAt: DateTimeOffset.UtcNow);

        return true;
    }

    private static CommitInfo ReadCommit(JsonElement commit)
    {
        var message = Text(commit, "comment") ?? "";
        var author = commit.TryGetProperty("author", out var element) ? element : default;

        return new CommitInfo(
            Sha: Text(commit, "commitId") ?? "",
            Message: message,
            AuthorName: author.ValueKind == JsonValueKind.Object ? Text(author, "name") ?? "" : "",
            AuthorEmail: author.ValueKind == JsonValueKind.Object ? Text(author, "email") : null,
            IsMerge: message.StartsWith("Merge pull request ", StringComparison.Ordinal)
                     || message.StartsWith("Merged PR ", StringComparison.Ordinal),
            CommittedAt: author.ValueKind == JsonValueKind.Object
                         && author.TryGetProperty("date", out var date)
                         && date.TryGetDateTimeOffset(out var at)
                ? at
                : DateTimeOffset.UtcNow);
    }

    /// <summary>Azure DevOps reports branches as full refs; the rest of the system wants the name.</summary>
    private static string? StripRef(string? reference) =>
        reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
            ? reference["refs/heads/".Length..]
            : reference;

    private static ActorInfo ReadActor(JsonElement resource, string property) =>
        resource.TryGetProperty(property, out var actor)
            ? new ActorInfo(Text(actor, "displayName") ?? "", Text(actor, "uniqueName"))
            : new ActorInfo("", null);

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
