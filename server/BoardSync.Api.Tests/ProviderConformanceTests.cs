using System.Security.Cryptography;
using System.Text;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Providers;
using Microsoft.AspNetCore.Http;

namespace BoardSync.Api.Tests;

/// <summary>
/// One set of scenarios, run against every provider adapter.
/// </summary>
/// <remarks>
/// <para>
/// The point of <see cref="IGitProvider"/> is that four hosts which disagree about naming cannot put
/// four vocabularies into the domain. That only holds if every adapter answers the same questions the
/// same way — and the failure mode without this suite is not a compile error, it is a second provider
/// that quietly resolves work items on the wrong signal.
/// </para>
/// <para>
/// The three adapters get these scenarios right in three genuinely different ways, which is exactly
/// why they need testing against one contract rather than each against its own payloads:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>GitHub</b> sends <c>closed</c> for both a merge and an abandonment, distinguished by a
///     <c>merged</c> boolean.
///   </description></item>
///   <item><description>
///     <b>GitLab</b> distinguishes them in the action name itself — <c>merge</c> versus
///     <c>close</c>.
///   </description></item>
///   <item><description>
///     <b>Azure DevOps</b> raises <c>git.pullrequest.merged</c> for the speculative conflict-check
///     merge as well as a real one, and only <c>status: completed</c> says the pull request actually
///     landed.
///   </description></item>
/// </list>
/// <para>
/// Getting any of those backwards resolves work that was thrown away, which is the most damaging
/// mistake an adapter can make.
/// </para>
/// </remarks>
public class ProviderConformanceTests
{
    private const string Secret = "a-shared-installation-secret";

    /// <summary>
    /// Everything an adapter needs to be exercised: how to sign a request, and one payload per
    /// scenario.
    /// </summary>
    private abstract class ProviderCase
    {
        public abstract IGitProvider Provider { get; }
        public abstract WebhookVerification ExpectedVerification { get; }

        /// <summary>Headers a genuine delivery would carry, including whatever proves it.</summary>
        public abstract IHeaderDictionary Authentic(byte[] body, string eventName);

        /// <summary>The same, but with a credential this installation would not accept.</summary>
        public abstract IHeaderDictionary Forged(byte[] body, string eventName);

        public abstract string PushPayload(string branch, string message);
        public abstract string BranchCreatedPayload(string branch);
        public abstract string PullRequestOpenedPayload(string source, string target);
        public abstract string PullRequestMergedPayload(string source, string target);
        public abstract string PullRequestAbandonedPayload(string source, string target);

        /// <summary>The provider's own name for a push, as its headers report it.</summary>
        public abstract string PushEventName { get; }

        /// <summary>The provider's own name for a pull request event.</summary>
        public abstract string PullRequestEventName { get; }

        public const string RepositoryId = "424242";
        public const string RepositoryName = "acme/payments";

        public override string ToString() => Provider.Provider.ToString();
    }

    public static TheoryData<string> Providers() => ["GitHub", "GitLab", "AzureDevOps"];

    private static ProviderCase CaseFor(string name) => name switch
    {
        "GitHub" => new GitHubCase(),
        "GitLab" => new GitLabCase(),
        "AzureDevOps" => new AzureDevOpsCase(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No conformance case.")
    };

    private static NormalizedGitEvent Normalize(ProviderCase c, string eventName, string payload)
    {
        Assert.True(c.Provider.TryNormalize(eventName, payload, out var normalized),
            $"{c} did not normalize a payload it should have.");

        return normalized;
    }

    // ── Verification ──────────────────────────────────────────────────────────

    /// <summary>A genuine delivery is accepted.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public void AGenuineDeliveryVerifies(string provider)
    {
        var c = CaseFor(provider);
        var body = Encoding.UTF8.GetBytes(c.PushPayload("bs-1-work", "BS-1 work"));

        Assert.True(c.Provider.Verify(body, c.Authentic(body, c.PushEventName), Secret));
    }

    /// <summary>A forged or absent credential is refused, by every provider.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public void AForgedCredentialIsRefused(string provider)
    {
        var c = CaseFor(provider);
        var body = Encoding.UTF8.GetBytes(c.PushPayload("bs-1-work", "BS-1 work"));

        Assert.False(c.Provider.Verify(body, c.Forged(body, c.PushEventName), Secret));
        Assert.False(c.Provider.Verify(body, new HeaderDictionary(), Secret));
    }

    /// <summary>
    /// Each adapter reports the verification strength it actually offers.
    /// </summary>
    /// <remarks>
    /// Not uniform, and the difference is recorded per delivery so an audit can answer what a given
    /// event was trusted on. A provider silently reporting a stronger level than it provides would
    /// make that record a lie.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void VerificationStrengthIsReportedHonestly(string provider)
    {
        var c = CaseFor(provider);
        Assert.Equal(c.ExpectedVerification, c.Provider.Verification);
    }

    /// <summary>
    /// Only GitHub can prove the payload was not altered in transit.
    /// </summary>
    /// <remarks>
    /// The asymmetry stated as a test rather than left in prose. GitHub's HMAC covers the body, so a
    /// tampered payload fails; GitLab's token and Azure DevOps' Basic credential say nothing about
    /// the body, so a tampered payload with a valid credential still verifies. That is the provider's
    /// ceiling, not a defect — but it must be known, because it is why an Azure DevOps merge wants
    /// corroborating before it resolves anything.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void OnlySignedProvidersDetectATamperedBody(string provider)
    {
        var c = CaseFor(provider);

        var original = Encoding.UTF8.GetBytes(c.PushPayload("bs-1-work", "BS-1 work"));
        var tampered = Encoding.UTF8.GetBytes(c.PushPayload("bs-999-elsewhere", "BS-999 work"));

        // Headers built for the original body, presented with a different one.
        var headers = c.Authentic(original, c.PushEventName);
        var detected = !c.Provider.Verify(tampered, headers, Secret);

        Assert.Equal(c.ExpectedVerification == WebhookVerification.HmacSha256, detected);
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    /// <summary>A push carries its branch, repository and commits, whatever the provider calls them.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public void APushNormalizesToTheSameShape(string provider)
    {
        var c = CaseFor(provider);

        var e = Normalize(c, c.PushEventName, c.PushPayload("bs-142-fix-login", "BS-142 start"));

        Assert.Equal(GitEventKind.Push, e.Kind);
        Assert.Equal("bs-142-fix-login", e.BranchName);
        Assert.Equal(ProviderCase.RepositoryId, e.RepositoryExternalId);
        Assert.Equal(ProviderCase.RepositoryName, e.RepositoryName);

        var commit = Assert.Single(e.Commits);
        Assert.Equal("BS-142 start", commit.Message);
        Assert.False(commit.IsMerge);
    }

    /// <summary>Branch names arrive bare, not as refs.</summary>
    /// <remarks>
    /// GitHub and GitLab send <c>refs/heads/x</c> on a push; Azure DevOps sends it on both a push and
    /// a pull request. The binding rules parse branch names, so a leaked <c>refs/heads/</c> prefix
    /// would silently stop every reference resolving.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void BranchNamesAreBare(string provider)
    {
        var c = CaseFor(provider);

        Assert.DoesNotContain("refs/heads/",
            Normalize(c, c.PushEventName, c.PushPayload("bs-1-work", "BS-1")).BranchName);

        var pr = Normalize(c, c.PullRequestEventName, c.PullRequestOpenedPayload("bs-1-work", "main"));

        Assert.Equal("bs-1-work", pr.BranchName);
        Assert.Equal("main", pr.TargetBranch);
    }

    /// <summary>A branch created with no commits is its own kind.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public void ABranchCreationIsDistinguishedFromAPush(string provider)
    {
        var c = CaseFor(provider);

        Assert.Equal(GitEventKind.BranchCreated,
            Normalize(c, c.PushEventName, c.BranchCreatedPayload("bs-200-new")).Kind);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void AnOpenedPullRequestNormalizes(string provider)
    {
        var c = CaseFor(provider);

        var e = Normalize(c, c.PullRequestEventName, c.PullRequestOpenedPayload("bs-1-work", "main"));

        Assert.Equal(GitEventKind.PullRequestOpened, e.Kind);
        Assert.NotNull(e.PullRequest);
        Assert.False(e.PullRequest!.Merged);
    }

    /// <summary>
    /// A merge and an abandonment are never confused, however the provider expresses the difference.
    /// </summary>
    /// <remarks>
    /// The single most damaging mistake an adapter could make: resolving work that was thrown away.
    /// Each of the three encodes it differently, so this is the scenario that most needs one test
    /// rather than three.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void AMergeAndAnAbandonmentAreDifferentEvents(string provider)
    {
        var c = CaseFor(provider);

        var merged = Normalize(c, c.PullRequestEventName, c.PullRequestMergedPayload("bs-1-work", "main"));
        var abandoned = Normalize(c, c.PullRequestEventName, c.PullRequestAbandonedPayload("bs-1-work", "main"));

        Assert.Equal(GitEventKind.PullRequestMerged, merged.Kind);
        Assert.True(merged.PullRequest!.Merged);

        Assert.Equal(GitEventKind.PullRequestClosed, abandoned.Kind);
        Assert.False(abandoned.PullRequest!.Merged);
    }

    /// <summary>The target branch survives, because a merge only resolves on the default one.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public void TheTargetBranchIsCarriedOnAMerge(string provider)
    {
        var c = CaseFor(provider);

        Assert.Equal("main",
            Normalize(c, c.PullRequestEventName, c.PullRequestMergedPayload("bs-1-work", "main")).TargetBranch);
    }

    /// <summary>
    /// An event nobody acts on is refused rather than throwing.
    /// </summary>
    /// <remarks>
    /// Every provider sends things BoardSync does not care about, and treating one as an error makes
    /// the delivery retry — enough failures disables the hook on some hosts.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void UnhandledEventsAreRefusedNotThrown(string provider)
    {
        var c = CaseFor(provider);

        Assert.False(c.Provider.TryNormalize("something-else", """{"zen":"Design for failure."}""", out _));
        Assert.False(c.Provider.TryNormalize(c.PushEventName, "{}", out _));
    }

    // ── The three cases ───────────────────────────────────────────────────────

    private sealed class GitHubCase : ProviderCase
    {
        public override IGitProvider Provider { get; } = new GitHubProvider();
        public override WebhookVerification ExpectedVerification => WebhookVerification.HmacSha256;
        public override string PushEventName => "push";
        public override string PullRequestEventName => "pull_request";

        public override IHeaderDictionary Authentic(byte[] body, string eventName) => new HeaderDictionary
        {
            ["X-GitHub-Event"] = eventName,
            ["X-GitHub-Delivery"] = Guid.NewGuid().ToString(),
            ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body))
        };

        public override IHeaderDictionary Forged(byte[] body, string eventName) => new HeaderDictionary
        {
            ["X-GitHub-Event"] = eventName,
            ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes("not-the-secret"), body))
        };

        public override string PushPayload(string branch, string message) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "created": false,
          "repository": { "id": {{RepositoryId}}, "full_name": "{{RepositoryName}}" },
          "pusher": { "name": "ada", "email": "ada@acme.test" },
          "commits": [
            { "id": "abc123", "message": "{{message}}",
              "author": { "name": "Ada", "email": "ada@acme.test" },
              "timestamp": "2026-08-24T10:00:00Z" }
          ]
        }
        """;

        public override string BranchCreatedPayload(string branch) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "created": true,
          "repository": { "id": {{RepositoryId}}, "full_name": "{{RepositoryName}}" },
          "pusher": { "name": "ada" },
          "commits": []
        }
        """;

        public override string PullRequestOpenedPayload(string source, string target) =>
            PullRequest("opened", merged: false, source, target);

        public override string PullRequestMergedPayload(string source, string target) =>
            PullRequest("closed", merged: true, source, target);

        public override string PullRequestAbandonedPayload(string source, string target) =>
            PullRequest("closed", merged: false, source, target);

        private static string PullRequest(string action, bool merged, string source, string target) => $$"""
        {
          "action": "{{action}}",
          "repository": { "id": {{RepositoryId}}, "full_name": "{{RepositoryName}}" },
          "sender": { "login": "ada" },
          "pull_request": {
            "number": 42, "title": "work", "body": null,
            "html_url": "https://github.com/acme/payments/pull/42",
            "merged": {{(merged ? "true" : "false")}},
            "head": { "ref": "{{source}}" }, "base": { "ref": "{{target}}" }
          }
        }
        """;
    }

    private sealed class GitLabCase : ProviderCase
    {
        public override IGitProvider Provider { get; } = new GitLabProvider();
        public override WebhookVerification ExpectedVerification => WebhookVerification.SharedSecret;
        public override string PushEventName => "Push Hook";
        public override string PullRequestEventName => "Merge Request Hook";

        public override IHeaderDictionary Authentic(byte[] body, string eventName) => new HeaderDictionary
        {
            ["X-Gitlab-Event"] = eventName,
            ["X-Gitlab-Token"] = Secret
        };

        public override IHeaderDictionary Forged(byte[] body, string eventName) => new HeaderDictionary
        {
            ["X-Gitlab-Event"] = eventName,
            ["X-Gitlab-Token"] = "not-the-secret"
        };

        public override string PushPayload(string branch, string message) => $$"""
        {
          "object_kind": "push",
          "before": "9d887a1",
          "ref": "refs/heads/{{branch}}",
          "user_username": "ada",
          "user_email": "ada@acme.test",
          "project": { "id": {{RepositoryId}}, "path_with_namespace": "{{RepositoryName}}" },
          "commits": [
            { "id": "abc123", "message": "{{message}}",
              "author": { "name": "Ada", "email": "ada@acme.test" },
              "timestamp": "2026-08-24T10:00:00Z" }
          ]
        }
        """;

        public override string BranchCreatedPayload(string branch) => $$"""
        {
          "object_kind": "push",
          "before": "0000000000000000000000000000000000000000",
          "ref": "refs/heads/{{branch}}",
          "user_username": "ada",
          "project": { "id": {{RepositoryId}}, "path_with_namespace": "{{RepositoryName}}" },
          "commits": []
        }
        """;

        public override string PullRequestOpenedPayload(string source, string target) =>
            MergeRequest("open", source, target);

        public override string PullRequestMergedPayload(string source, string target) =>
            MergeRequest("merge", source, target);

        public override string PullRequestAbandonedPayload(string source, string target) =>
            MergeRequest("close", source, target);

        private static string MergeRequest(string action, string source, string target) => $$"""
        {
          "object_kind": "merge_request",
          "project": { "id": {{RepositoryId}}, "path_with_namespace": "{{RepositoryName}}" },
          "user": { "username": "ada", "email": "ada@acme.test" },
          "object_attributes": {
            "iid": 42, "title": "work", "description": null,
            "url": "https://gitlab.com/acme/payments/-/merge_requests/42",
            "action": "{{action}}",
            "source_branch": "{{source}}", "target_branch": "{{target}}"
          }
        }
        """;
    }

    private sealed class AzureDevOpsCase : ProviderCase
    {
        public override IGitProvider Provider { get; } = new AzureDevOpsProvider();
        public override WebhookVerification ExpectedVerification => WebhookVerification.BasicAuth;
        public override string PushEventName => "azuredevops";
        public override string PullRequestEventName => "azuredevops";

        private static string Basic(string password) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"boardsync:{password}"));

        public override IHeaderDictionary Authentic(byte[] body, string eventName) =>
            new HeaderDictionary { ["Authorization"] = Basic(Secret) };

        public override IHeaderDictionary Forged(byte[] body, string eventName) =>
            new HeaderDictionary { ["Authorization"] = Basic("not-the-secret") };

        public override string PushPayload(string branch, string message) => $$"""
        {
          "eventType": "git.push",
          "resource": {
            "repository": { "id": "{{RepositoryId}}", "name": "{{RepositoryName}}",
                            "defaultBranch": "refs/heads/main" },
            "refUpdates": [ { "name": "refs/heads/{{branch}}" } ],
            "pushedBy": { "displayName": "Ada", "uniqueName": "ada@acme.test" },
            "commits": [
              { "commitId": "abc123", "comment": "{{message}}",
                "author": { "name": "Ada", "email": "ada@acme.test",
                            "date": "2026-08-24T10:00:00Z" } }
            ]
          }
        }
        """;

        public override string BranchCreatedPayload(string branch) => $$"""
        {
          "eventType": "git.push",
          "resource": {
            "repository": { "id": "{{RepositoryId}}", "name": "{{RepositoryName}}" },
            "refUpdates": [ { "name": "refs/heads/{{branch}}" } ],
            "pushedBy": { "displayName": "Ada" },
            "commits": []
          }
        }
        """;

        public override string PullRequestOpenedPayload(string source, string target) =>
            PullRequest("git.pullrequest.created", "active", source, target);

        public override string PullRequestMergedPayload(string source, string target) =>
            PullRequest("git.pullrequest.merged", "completed", source, target);

        public override string PullRequestAbandonedPayload(string source, string target) =>
            PullRequest("git.pullrequest.updated", "abandoned", source, target);

        private static string PullRequest(
            string eventType, string status, string source, string target) => $$"""
        {
          "eventType": "{{eventType}}",
          "resource": {
            "repository": { "id": "{{RepositoryId}}", "name": "{{RepositoryName}}" },
            "pullRequestId": 42,
            "status": "{{status}}",
            "title": "work",
            "description": null,
            "sourceRefName": "refs/heads/{{source}}",
            "targetRefName": "refs/heads/{{target}}",
            "mergeStatus": "succeeded",
            "createdBy": { "displayName": "Ada", "uniqueName": "ada@acme.test" },
            "url": "https://dev.azure.com/acme/_apis/git/repositories/x/pullRequests/42"
          }
        }
        """;
    }

    // ── Provider-specific traps, stated once ──────────────────────────────────

    /// <summary>
    /// Azure DevOps raises <c>git.pullrequest.merged</c> for its speculative conflict check, and only
    /// <c>status: completed</c> means the pull request landed.
    /// </summary>
    /// <remarks>
    /// Outside the shared scenarios because no other provider has an equivalent, and it is the single
    /// most dangerous thing about this adapter: acting on the event name alone would resolve a work
    /// item the moment somebody opened a pull request.
    /// </remarks>
    [Fact]
    public void AzureDevOpsMergeAttemptOnAnOpenPullRequestIsNotAMerge()
    {
        var provider = new AzureDevOpsProvider();

        var speculative = """
        {
          "eventType": "git.pullrequest.merged",
          "resource": {
            "repository": { "id": "1", "name": "acme/payments" },
            "pullRequestId": 42, "status": "active", "title": "work",
            "sourceRefName": "refs/heads/bs-1-work", "targetRefName": "refs/heads/main",
            "mergeStatus": "succeeded"
          }
        }
        """;

        Assert.False(provider.TryNormalize("azuredevops", speculative, out _));
    }

    /// <summary>
    /// GitLab reports a branch creation with an all-zero before-hash, not a boolean.
    /// </summary>
    [Fact]
    public void GitLabDistinguishesBranchCreationByItsBeforeHash()
    {
        var provider = new GitLabProvider();

        var payload = """
        {
          "object_kind": "push",
          "before": "0000000000000000000000000000000000000000",
          "ref": "refs/heads/bs-1-new",
          "project": { "id": 1, "path_with_namespace": "acme/payments" },
          "commits": []
        }
        """;

        Assert.True(provider.TryNormalize("Push Hook", payload, out var normalized));
        Assert.Equal(GitEventKind.BranchCreated, normalized.Kind);
    }
}
