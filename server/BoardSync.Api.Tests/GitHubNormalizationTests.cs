using BoardSync.Api.Modules.GitSync.Providers;

namespace BoardSync.Api.Tests;

/// <summary>
/// That GitHub's payloads become BoardSync's own vocabulary, and that the distinctions the workflow
/// depends on survive the translation.
/// </summary>
/// <remarks>
/// The provider port exists so four hosts that disagree about naming — pull request versus merge
/// request, repository versus project — cannot put four vocabularies into the domain. These check the
/// translation actually preserves what the state machine will act on: which branch, whether a pull
/// request merged or was abandoned, and which commits carry it.
/// </remarks>
public class GitHubNormalizationTests
{
    private static readonly GitHubProvider Provider = new();

    private static NormalizedGitEvent Normalize(string eventName, string payload)
    {
        Assert.True(Provider.TryNormalize(eventName, payload, out var normalized),
            $"Expected '{eventName}' to normalize.");

        return normalized;
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    private const string PushPayload = """
    {
      "ref": "refs/heads/bs-142-fix-login",
      "created": false,
      "repository": { "id": 987654, "full_name": "acme/payments" },
      "pusher": { "name": "ada", "email": "ada@acme.test" },
      "commits": [
        { "id": "abc123", "message": "BS-142 start on login",
          "author": { "name": "Ada", "email": "ada@acme.test" },
          "timestamp": "2026-08-23T10:00:00Z" },
        { "id": "def456", "message": "BS-142 handle the empty case",
          "author": { "name": "Ada", "email": "ada@acme.test" },
          "timestamp": "2026-08-23T11:00:00Z" }
      ]
    }
    """;

    [Fact]
    public void APushCarriesItsBranchRepositoryAndCommits()
    {
        var e = Normalize("push", PushPayload);

        Assert.Equal(GitEventKind.Push, e.Kind);
        Assert.Equal("bs-142-fix-login", e.BranchName);
        Assert.Equal("987654", e.RepositoryExternalId);
        Assert.Equal("acme/payments", e.RepositoryName);
        Assert.Equal(2, e.Commits.Count);
        Assert.Equal("ada", e.Actor.Login);
    }

    /// <summary>
    /// Commit messages survive intact, because the binding rules read them.
    /// </summary>
    /// <remarks>
    /// The next increment resolves <c>BS-142</c> out of these. Truncating or normalizing the message
    /// here would silently break that, and would do so in a way that looked like a binding bug rather
    /// than a parsing one.
    /// </remarks>
    [Fact]
    public void CommitMessagesArePreservedForBinding()
    {
        var e = Normalize("push", PushPayload);

        Assert.Equal("BS-142 start on login", e.Commits[0].Message);
        Assert.Equal("abc123", e.Commits[0].Sha);
        Assert.Equal("ada@acme.test", e.Commits[0].AuthorEmail);
    }

    /// <summary>
    /// The event is timestamped from the payload, not from when it was processed.
    /// </summary>
    /// <remarks>
    /// Load-bearing for the "a human overrode this" rule the transitions will apply: webhooks arrive
    /// out of order routinely, so comparing against processing time would let a late delivery look
    /// newer than the manual change that superseded it.
    /// </remarks>
    [Fact]
    public void APushIsTimestampedFromItsLastCommit() =>
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-23T11:00:00Z"),
            Normalize("push", PushPayload).OccurredAt);

    /// <summary>Tag pushes are not branch activity and are ignored.</summary>
    [Fact]
    public void ATagPushIsIgnored() =>
        Assert.False(Provider.TryNormalize("push", """
        {
          "ref": "refs/tags/v1.2.0",
          "repository": { "id": 1, "full_name": "acme/payments" },
          "commits": []
        }
        """, out _));

    /// <summary>
    /// Creating a branch is distinguished from pushing to one.
    /// </summary>
    /// <remarks>
    /// It is the earliest moment a branch name can bind work — before anybody has committed — which
    /// is what lets the board move as soon as someone starts, rather than at their first commit.
    /// </remarks>
    [Fact]
    public void CreatingABranchIsItsOwnKind() =>
        Assert.Equal(GitEventKind.BranchCreated, Normalize("push", """
        {
          "ref": "refs/heads/bs-200-new-work",
          "created": true,
          "repository": { "id": 1, "full_name": "acme/payments" },
          "pusher": { "name": "ada" },
          "commits": []
        }
        """).Kind);

    /// <summary>
    /// Merge commits are flagged, because binding skips them.
    /// </summary>
    /// <remarks>
    /// A merge is not authorship. Binding through one would attribute every commit on the branch to
    /// whoever pressed the merge button.
    /// </remarks>
    [Fact]
    public void MergeCommitsAreFlagged()
    {
        var e = Normalize("push", """
        {
          "ref": "refs/heads/main",
          "repository": { "id": 1, "full_name": "acme/payments" },
          "pusher": { "name": "ada" },
          "commits": [
            { "id": "m1", "message": "Merge pull request #7 from acme/bs-142",
              "author": { "name": "Ada" }, "timestamp": "2026-08-23T12:00:00Z" },
            { "id": "c1", "message": "BS-142 real work",
              "author": { "name": "Ada" }, "timestamp": "2026-08-23T11:00:00Z" }
          ]
        }
        """);

        Assert.True(e.Commits[0].IsMerge);
        Assert.False(e.Commits[1].IsMerge);
    }

    // ── Pull requests ─────────────────────────────────────────────────────────

    private static string PullRequest(string action, bool merged) => $$"""
    {
      "action": "{{action}}",
      "repository": { "id": 987654, "full_name": "acme/payments" },
      "sender": { "login": "ada" },
      "pull_request": {
        "number": 42,
        "title": "BS-142 fix login",
        "body": "Closes the empty-password case.",
        "html_url": "https://github.com/acme/payments/pull/42",
        "merged": {{(merged ? "true" : "false")}},
        "head": { "ref": "bs-142-fix-login" },
        "base": { "ref": "main" }
      }
    }
    """;

    [Fact]
    public void AnOpenedPullRequestBecomesPullRequestOpened()
    {
        var e = Normalize("pull_request", PullRequest("opened", merged: false));

        Assert.Equal(GitEventKind.PullRequestOpened, e.Kind);
        Assert.Equal("bs-142-fix-login", e.BranchName);
        Assert.Equal("main", e.TargetBranch);
        Assert.Equal(42, e.PullRequest!.Number);
        Assert.Equal("BS-142 fix login", e.PullRequest.Title);
    }

    /// <summary>
    /// A merge and an abandonment arrive under the same action and must not be confused.
    /// </summary>
    /// <remarks>
    /// GitHub sends <c>closed</c> for both; only the <c>merged</c> flag separates them. Getting this
    /// backwards would resolve work that was thrown away, which is the single most damaging mistake
    /// this adapter could make.
    /// </remarks>
    [Fact]
    public void MergedAndAbandonedPullRequestsAreDifferentEvents()
    {
        Assert.Equal(GitEventKind.PullRequestMerged,
            Normalize("pull_request", PullRequest("closed", merged: true)).Kind);

        Assert.Equal(GitEventKind.PullRequestClosed,
            Normalize("pull_request", PullRequest("closed", merged: false)).Kind);
    }

    /// <summary>
    /// The base branch survives, because a merge only means "done" when it lands on the default one.
    /// </summary>
    [Fact]
    public void TheTargetBranchIsCarriedSoAMergeCanBeJudged() =>
        Assert.Equal("main", Normalize("pull_request", PullRequest("closed", merged: true)).TargetBranch);

    [Theory]
    [InlineData("assigned")]
    [InlineData("labeled")]
    [InlineData("synchronize")]
    public void UninterestingPullRequestActionsAreIgnored(string action) =>
        Assert.False(Provider.TryNormalize("pull_request", PullRequest(action, merged: false), out _));

    // ── Events nobody acts on ─────────────────────────────────────────────────

    /// <summary>
    /// Unhandled event types are a normal outcome, not a failure.
    /// </summary>
    /// <remarks>
    /// A GitHub App installation receives everything the account subscribes it to. Treating a star or
    /// a ping as an error would make the delivery retry, and enough retries disables the hook.
    /// </remarks>
    [Theory]
    [InlineData("ping")]
    [InlineData("star")]
    [InlineData("fork")]
    [InlineData("issues")]
    public void UnhandledEventTypesReturnFalseRatherThanThrowing(string eventName) =>
        Assert.False(Provider.TryNormalize(eventName, """
        { "repository": { "id": 1, "full_name": "acme/payments" } }
        """, out _));

    /// <summary>A payload with no repository cannot be acted on and is refused.</summary>
    [Fact]
    public void APayloadWithoutARepositoryIsRefused() =>
        Assert.False(Provider.TryNormalize("push", """{"ref":"refs/heads/main"}""", out _));
}
