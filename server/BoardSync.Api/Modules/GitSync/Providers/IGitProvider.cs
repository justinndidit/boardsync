using BoardSync.Api.Modules.GitSync.Models;

namespace BoardSync.Api.Modules.GitSync.Providers;

/// <summary>
/// What every git host must be able to tell BoardSync.
/// </summary>
/// <remarks>
/// <para>
/// Load-bearing rather than defensive: GitHub, GitLab, Azure DevOps and Bitbucket are all in scope,
/// and they disagree about enough — pull request versus merge request, repository versus project,
/// signed versus unsigned webhooks — that letting any of it reach the rest of the system would put
/// four vocabularies into the domain. Everything downstream of <see cref="TryNormalize"/> sees only
/// <see cref="NormalizedGitEvent"/>.
/// </para>
/// <para>
/// A conformance suite runs the same scenarios against every implementation, so an adapter added
/// later has to answer the same questions rather than merely compile.
/// </para>
/// </remarks>
public interface IGitProvider
{
    /// <summary>Which host this handles.</summary>
    GitProvider Provider { get; }

    /// <summary>
    /// The strongest verification this host offers.
    /// </summary>
    /// <remarks>
    /// Not uniform, and the difference matters: three of the four sign the payload, and Azure DevOps
    /// cannot. Recorded per delivery so an audit can tell what a given event was trusted on.
    /// </remarks>
    WebhookVerification Verification { get; }

    /// <summary>
    /// Whether this request really came from the host, for this installation.
    /// </summary>
    /// <param name="rawBody">
    /// The exact bytes received. Signatures are over the bytes, so anything re-serialized will not
    /// verify — the ingest endpoint buffers the body once and passes it through untouched.
    /// </param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secret">This installation's own webhook secret.</param>
    bool Verify(ReadOnlySpan<byte> rawBody, IHeaderDictionary headers, string secret);

    /// <summary>The provider's name for this event, read from its headers.</summary>
    string? EventNameOf(IHeaderDictionary headers);

    /// <summary>The provider's delivery id, read from its headers. Null when it does not send one.</summary>
    string? DeliveryIdOf(IHeaderDictionary headers);

    /// <summary>
    /// Turns a raw payload into the shape the rest of the system understands.
    /// </summary>
    /// <returns>
    /// False when this is an event BoardSync does not act on — a star, a fork, a ping. That is a
    /// normal outcome, not a failure: the delivery is still recorded and still answered with a 2xx,
    /// because teaching a provider to retry events we deliberately ignore is how a webhook gets
    /// disabled.
    /// </returns>
    bool TryNormalize(string eventName, string rawBody, out NormalizedGitEvent normalized);
}

/// <summary>What kind of thing happened in the repository.</summary>
public enum GitEventKind
{
    /// <summary>Commits were pushed to a branch.</summary>
    Push,

    /// <summary>A branch was created with no commits of its own yet.</summary>
    BranchCreated,

    /// <summary>A pull request was opened, or reopened.</summary>
    PullRequestOpened,

    /// <summary>A pull request was merged.</summary>
    PullRequestMerged,

    /// <summary>A pull request was closed without merging.</summary>
    PullRequestClosed
}

/// <summary>
/// A git event in BoardSync's own terms.
/// </summary>
/// <remarks>
/// The module's domain type and the only shape anything outside <c>Providers</c> sees. Deliberately
/// says nothing about work items: binding a commit to a task is a decision BoardSync makes from its
/// own rules, not something a provider payload can assert.
/// </remarks>
/// <param name="Kind">What happened.</param>
/// <param name="RepositoryExternalId">The provider's stable repository id, which survives a rename.</param>
/// <param name="RepositoryName">Display name, e.g. <c>acme/payments</c>.</param>
/// <param name="BranchName">
/// The branch this concerns — the pushed branch, or a pull request's source branch. Null when the
/// event has no single branch.
/// </param>
/// <param name="TargetBranch">
/// A pull request's base branch, compared against the repository's default branch to decide whether
/// a merge means "done". Null for pushes.
/// </param>
/// <param name="Commits">The commits carried, oldest first. Empty for a branch creation.</param>
/// <param name="PullRequest">The pull request, when this is one.</param>
/// <param name="Actor">Who did it, in the provider's terms.</param>
/// <param name="OccurredAt">
/// When the provider says it happened. Used to decide whether a human has since overridden the
/// board, so it must come from the payload rather than from the clock at processing time — webhooks
/// arrive out of order routinely.
/// </param>
public sealed record NormalizedGitEvent(
    GitEventKind Kind,
    string RepositoryExternalId,
    string RepositoryName,
    string? BranchName,
    string? TargetBranch,
    IReadOnlyList<CommitInfo> Commits,
    PullRequestInfo? PullRequest,
    ActorInfo Actor,
    DateTimeOffset OccurredAt);

/// <param name="Sha">The commit hash.</param>
/// <param name="Message">The full message, subject and body — task references may be in either.</param>
/// <param name="AuthorName">The author's name as git records it.</param>
/// <param name="AuthorEmail">
/// The author's email as git records it. The only thread back to a BoardSync user, and it often
/// does not match one — external contributors and bots both commit.
/// </param>
/// <param name="IsMerge">
/// Whether this is a merge commit. Skipped when binding: a merge is not authorship, and treating it
/// as such attributes every branch's work to whoever pressed the button.
/// </param>
/// <param name="CommittedAt">When it was committed.</param>
public sealed record CommitInfo(
    string Sha,
    string Message,
    string AuthorName,
    string? AuthorEmail,
    bool IsMerge,
    DateTimeOffset CommittedAt);

/// <param name="Number">The pull request number as people refer to it.</param>
/// <param name="Title">Its title — a common place for a task reference.</param>
/// <param name="Body">Its description — likewise.</param>
/// <param name="Url">Link back to the provider, for the activity entry.</param>
/// <param name="Merged">Whether it was merged, as opposed to closed.</param>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    string? Body,
    string Url,
    bool Merged);

/// <param name="Login">The provider account name.</param>
/// <param name="Email">Their email, when the provider gives one.</param>
/// <remarks>
/// Attribution, never authority. What the integration may do comes from the installation's own
/// grant; who it says did it is metadata, resolved to a BoardSync user when the email matches one
/// and left as a display string when it does not.
/// </remarks>
public sealed record ActorInfo(string Login, string? Email);
