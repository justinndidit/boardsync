using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardSync.Api.Modules.GitSync.Models;

namespace BoardSync.Api.Modules.GitSync.Providers;

/// <summary>
/// GitHub, via a GitHub App.
/// </summary>
/// <remarks>
/// <para>
/// <b>A GitHub App, not an OAuth App.</b> Webhooks are configured once per installation and cover
/// every repository the app can see, rather than needing setup per repository; permissions are
/// fine-grained; tokens are short-lived; rate limits scale with the installation. The one that
/// decides it for a product: an App keeps working when the person who installed it leaves the
/// organization, and an OAuth App does not.
/// </para>
/// <para>
/// Signs deliveries with HMAC-SHA256 over the raw body, so a verified delivery proves both origin
/// and that the payload was not altered in transit — the strongest of the four providers.
/// </para>
/// </remarks>
public sealed class GitHubProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.GitHub;

    public WebhookVerification Verification => WebhookVerification.HmacSha256;

    private const string SignatureHeader = "X-Hub-Signature-256";
    private const string EventHeader = "X-GitHub-Event";
    private const string DeliveryHeader = "X-GitHub-Delivery";

    public string? EventNameOf(IHeaderDictionary headers) =>
        headers.TryGetValue(EventHeader, out var value) ? value.ToString() : null;

    public string? DeliveryIdOf(IHeaderDictionary headers) =>
        headers.TryGetValue(DeliveryHeader, out var value) ? value.ToString() : null;

    /// <summary>
    /// Recomputes the signature over the exact bytes received and compares it in constant time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> rather than <c>==</c>: a byte-by-byte
    /// comparison that returns early leaks, through timing, how much of a guessed signature was
    /// right, which turns forging one into a per-byte search rather than a 2^256 one.
    /// </para>
    /// <para>
    /// Everything that could make this accept something it should not is treated as a failure —
    /// a missing header, a malformed prefix, a wrong length. There is no path through here that
    /// returns true without a matching digest.
    /// </para>
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> rawBody, IHeaderDictionary headers, string secret)
    {
        if (!headers.TryGetValue(SignatureHeader, out var header)) return false;

        var offered = header.ToString();

        // GitHub sends "sha256=<hex>". Anything else is not a signature we can check.
        const string prefix = "sha256=";
        if (!offered.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var offeredHex = offered.AsSpan(prefix.Length);

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, expected);

        Span<byte> provided = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (!TryParseHex(offeredHex, provided)) return false;

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>
    /// Parses a fixed-length hex string without allocating or throwing on bad input.
    /// </summary>
    /// <remarks>
    /// A wrong length is rejected before parsing rather than after, so a caller cannot use the
    /// difference between "too short" and "not hex" to learn anything.
    /// </remarks>
    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> destination) =>
        hex.Length == destination.Length * 2
        && Convert.FromHexString(hex, destination, out _, out var written) == OperationStatus.Done
        && written == destination.Length;

    public bool TryNormalize(string eventName, string rawBody, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("repository", out var repository)) return false;

        var repositoryId = repository.GetProperty("id").GetRawText().Trim('"');
        var repositoryName = repository.TryGetProperty("full_name", out var fullName)
            ? fullName.GetString() ?? ""
            : "";

        return eventName switch
        {
            "push" => TryNormalizePush(root, repositoryId, repositoryName, out normalized),
            "pull_request" => TryNormalizePullRequest(root, repositoryId, repositoryName, out normalized),

            // Everything else — ping, star, fork, issues. Not a failure; see IGitProvider.TryNormalize.
            _ => false
        };
    }

    private static bool TryNormalizePush(
        JsonElement root, string repositoryId, string repositoryName, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        var reference = root.TryGetProperty("ref", out var refElement) ? refElement.GetString() : null;

        // Tags and other refs are not branches, and nothing in the workflow is driven by them.
        if (reference is null || !reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            return false;

        var branch = reference["refs/heads/".Length..];

        var commits = root.TryGetProperty("commits", out var commitArray)
            ? commitArray.EnumerateArray().Select(ReadCommit).ToList()
            : [];

        // A push that creates a branch carries no commits of its own. Worth distinguishing: it is
        // the earliest moment a branch name can bind work, before anybody has committed to it.
        var created = root.TryGetProperty("created", out var createdElement) && createdElement.GetBoolean();

        normalized = new NormalizedGitEvent(
            Kind: created && commits.Count == 0 ? GitEventKind.BranchCreated : GitEventKind.Push,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: branch,
            TargetBranch: null,
            Commits: commits,
            PullRequest: null,
            Actor: ReadPusher(root),
            OccurredAt: commits.Count > 0 ? commits[^1].CommittedAt : DateTimeOffset.UtcNow);

        return true;
    }

    private static bool TryNormalizePullRequest(
        JsonElement root, string repositoryId, string repositoryName, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        if (!root.TryGetProperty("action", out var actionElement)) return false;
        if (!root.TryGetProperty("pull_request", out var pr)) return false;

        var action = actionElement.GetString();
        var merged = pr.TryGetProperty("merged", out var mergedElement) && mergedElement.GetBoolean();

        // "closed" is two different events wearing one name, and the difference is the whole point:
        // merged means the work landed, closed-unmerged means it did not.
        var kind = action switch
        {
            "opened" or "reopened" or "ready_for_review" => GitEventKind.PullRequestOpened,
            "closed" when merged => GitEventKind.PullRequestMerged,
            "closed" => GitEventKind.PullRequestClosed,
            _ => (GitEventKind?)null
        };

        if (kind is not { } eventKind) return false;

        normalized = new NormalizedGitEvent(
            Kind: eventKind,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: Text(pr, "head", "ref"),
            TargetBranch: Text(pr, "base", "ref"),
            Commits: [],
            PullRequest: new PullRequestInfo(
                Number: pr.GetProperty("number").GetInt32(),
                Title: pr.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                Body: pr.TryGetProperty("body", out var body) ? body.GetString() : null,
                Url: pr.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                Merged: merged),
            Actor: ReadSender(root),
            OccurredAt: DateTimeOffset.UtcNow);

        return true;
    }

    private static CommitInfo ReadCommit(JsonElement commit)
    {
        var author = commit.TryGetProperty("author", out var authorElement) ? authorElement : default;

        // GitHub reports a merge commit by giving it two parents; the push payload does not say so
        // directly, so the subject line is the available signal.
        var message = commit.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

        return new CommitInfo(
            Sha: commit.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Message: message,
            AuthorName: author.ValueKind == JsonValueKind.Object && author.TryGetProperty("name", out var name)
                ? name.GetString() ?? ""
                : "",
            AuthorEmail: author.ValueKind == JsonValueKind.Object && author.TryGetProperty("email", out var email)
                ? email.GetString()
                : null,
            IsMerge: message.StartsWith("Merge pull request ", StringComparison.Ordinal)
                     || message.StartsWith("Merge branch ", StringComparison.Ordinal),
            CommittedAt: commit.TryGetProperty("timestamp", out var timestamp)
                         && timestamp.TryGetDateTimeOffset(out var at)
                ? at
                : DateTimeOffset.UtcNow);
    }

    private static ActorInfo ReadPusher(JsonElement root) =>
        root.TryGetProperty("pusher", out var pusher)
            ? new ActorInfo(
                Text(pusher, "name") ?? "",
                Text(pusher, "email"))
            : ReadSender(root);

    private static ActorInfo ReadSender(JsonElement root) =>
        root.TryGetProperty("sender", out var sender)
            ? new ActorInfo(Text(sender, "login") ?? "", Text(sender, "email"))
            : new ActorInfo("", null);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static string? Text(JsonElement element, string first, string second) =>
        element.TryGetProperty(first, out var nested) ? Text(nested, second) : null;
}
