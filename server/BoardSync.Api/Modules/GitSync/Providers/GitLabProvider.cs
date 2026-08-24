using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardSync.Api.Modules.GitSync.Models;

namespace BoardSync.Api.Modules.GitSync.Providers;

/// <summary>
/// GitLab, via a project or group webhook.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verified by a shared secret, not a signature.</b> GitLab sends the configured secret verbatim
/// in <c>X-Gitlab-Token</c>, so a verified delivery proves the caller knew the secret and proves
/// nothing about the payload — unlike GitHub, where the HMAC covers the body. That is a real
/// difference in what a delivery can be trusted for, which is why
/// <see cref="WebhookVerification"/> is recorded per delivery rather than inferred from the
/// provider.
/// </para>
/// <para>
/// GitLab has since added signing tokens that compute an HMAC over the payload, which would make
/// this equivalent to GitHub. Supporting them is the obvious upgrade and is deliberately not guessed
/// at here: the header name and digest encoding need confirming against a real delivery rather than
/// against documentation, and getting a signature check subtly wrong is worse than not having one.
/// </para>
/// </remarks>
public sealed class GitLabProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.GitLab;

    public WebhookVerification Verification => WebhookVerification.SharedSecret;

    private const string TokenHeader = "X-Gitlab-Token";
    private const string EventHeader = "X-Gitlab-Event";

    /// <remarks>
    /// GitLab does not send a delivery id. <c>WebhookIngestService</c> derives one from the payload
    /// digest instead, so a redelivery of the same bytes still deduplicates.
    /// </remarks>
    public string? DeliveryIdOf(IHeaderDictionary headers) => null;

    public string? EventNameOf(IHeaderDictionary headers) =>
        headers.TryGetValue(EventHeader, out var value) ? value.ToString() : null;

    /// <summary>
    /// Compares the presented token against the installation's secret, in constant time.
    /// </summary>
    /// <remarks>
    /// Constant-time even though this is a plain equality check: a byte-by-byte comparison that
    /// returns early leaks how much of a guessed secret was right, which turns guessing it into a
    /// per-character search. The token is the whole credential here, so that matters more than it
    /// does where a signature also has to match.
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> rawBody, IHeaderDictionary headers, string secret)
    {
        if (!headers.TryGetValue(TokenHeader, out var header)) return false;

        var presented = Encoding.UTF8.GetBytes(header.ToString());
        var expected = Encoding.UTF8.GetBytes(secret);

        // FixedTimeEquals requires equal lengths, and a length mismatch is already a mismatch — but
        // returning early on it would leak the secret's length, so both are hashed to a fixed size
        // first and the comparison is over the digests.
        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(presented, presentedHash);
        SHA256.HashData(expected, expectedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    public bool TryNormalize(string eventName, string rawBody, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("project", out var project)) return false;

        var repositoryId = project.TryGetProperty("id", out var id) ? id.GetRawText().Trim('"') : "";
        var repositoryName = Text(project, "path_with_namespace") ?? Text(project, "name") ?? "";

        if (repositoryId.Length == 0) return false;

        // object_kind rather than the header, because the header is prose ("Push Hook") and the
        // payload's own discriminator is not.
        return Text(root, "object_kind") switch
        {
            "push" => TryNormalizePush(root, repositoryId, repositoryName, out normalized),
            "merge_request" => TryNormalizeMergeRequest(root, repositoryId, repositoryName, out normalized),
            _ => false
        };
    }

    private static bool TryNormalizePush(
        JsonElement root, string repositoryId, string repositoryName, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        var reference = Text(root, "ref");

        if (reference is null || !reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            return false;

        var commits = root.TryGetProperty("commits", out var array)
            ? array.EnumerateArray().Select(ReadCommit).ToList()
            : [];

        // GitLab marks a branch creation with an all-zero before-hash rather than a boolean.
        var created = Text(root, "before") is { } before && before.All(c => c == '0');

        normalized = new NormalizedGitEvent(
            Kind: created && commits.Count == 0 ? GitEventKind.BranchCreated : GitEventKind.Push,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: reference["refs/heads/".Length..],
            TargetBranch: null,
            Commits: commits,
            PullRequest: null,
            Actor: new ActorInfo(Text(root, "user_username") ?? "", Text(root, "user_email")),
            OccurredAt: commits.Count > 0 ? commits[^1].CommittedAt : DateTimeOffset.UtcNow);

        return true;
    }

    private static bool TryNormalizeMergeRequest(
        JsonElement root, string repositoryId, string repositoryName, out NormalizedGitEvent normalized)
    {
        normalized = null!;

        if (!root.TryGetProperty("object_attributes", out var attributes)) return false;

        // GitLab distinguishes merge from close in the action itself, so unlike GitHub there is no
        // flag to disambiguate one name covering two outcomes.
        var kind = Text(attributes, "action") switch
        {
            "open" or "reopen" => GitEventKind.PullRequestOpened,
            "merge" => GitEventKind.PullRequestMerged,
            "close" => GitEventKind.PullRequestClosed,
            _ => (GitEventKind?)null
        };

        if (kind is not { } eventKind) return false;

        normalized = new NormalizedGitEvent(
            Kind: eventKind,
            RepositoryExternalId: repositoryId,
            RepositoryName: repositoryName,
            BranchName: Text(attributes, "source_branch"),
            TargetBranch: Text(attributes, "target_branch"),
            Commits: [],
            PullRequest: new PullRequestInfo(
                Number: attributes.TryGetProperty("iid", out var iid) && iid.TryGetInt32(out var number)
                    ? number
                    : 0,
                Title: Text(attributes, "title") ?? "",
                Body: Text(attributes, "description"),
                Url: Text(attributes, "url") ?? "",
                Merged: eventKind == GitEventKind.PullRequestMerged),
            Actor: root.TryGetProperty("user", out var user)
                ? new ActorInfo(Text(user, "username") ?? "", Text(user, "email"))
                : new ActorInfo("", null),
            OccurredAt: DateTimeOffset.UtcNow);

        return true;
    }

    private static CommitInfo ReadCommit(JsonElement commit)
    {
        var message = Text(commit, "message") ?? "";
        var author = commit.TryGetProperty("author", out var element) ? element : default;

        return new CommitInfo(
            Sha: Text(commit, "id") ?? "",
            Message: message,
            AuthorName: author.ValueKind == JsonValueKind.Object ? Text(author, "name") ?? "" : "",
            AuthorEmail: author.ValueKind == JsonValueKind.Object ? Text(author, "email") : null,
            IsMerge: message.StartsWith("Merge branch ", StringComparison.Ordinal)
                     || message.StartsWith("Merge remote-tracking branch ", StringComparison.Ordinal),
            CommittedAt: commit.TryGetProperty("timestamp", out var timestamp)
                         && timestamp.TryGetDateTimeOffset(out var at)
                ? at
                : DateTimeOffset.UtcNow);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
