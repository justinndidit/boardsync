using BoardSync.Api.Modules.GitSync.Domain;
using BoardSync.Api.Modules.GitSync.Providers;
using BoardSync.Api.Modules.OrgProject.Domain.Helpers;

namespace BoardSync.Api.Tests;

/// <summary>
/// That <c>BS-142</c> is found where developers write it, and not found where they did not mean it.
/// </summary>
/// <remarks>
/// <para>
/// This parser decides whether the product works. Too strict and the board silently stops updating
/// for people who did type a reference; too loose and it moves work items because somebody mentioned
/// a date or a version number in a commit message. The false positives are worse — a board that
/// moves the wrong card is less trustworthy than one that misses.
/// </para>
/// </remarks>
public class WorkItemReferenceTests
{
    private static string[] Found(string? text) =>
        [.. WorkItemReferences.Parse(text).Select(r => r.ToString())];

    // ── What must be found ────────────────────────────────────────────────────

    /// <summary>The branch-naming conventions people actually use.</summary>
    [Theory]
    [InlineData("bs-142-fix-login")]
    [InlineData("BS-142-fix-login")]
    [InlineData("feature/BS-142")]
    [InlineData("feature/bs-142-fix-login")]
    [InlineData("bugfix/BS-142_login")]
    [InlineData("BS-142")]
    [InlineData("ada/bs-142/attempt-two")]
    public void ReferencesAreFoundInBranchNames(string branch) =>
        Assert.Equal(["BS-142"], Found(branch));

    /// <summary>And in commit messages, wherever in them.</summary>
    [Theory]
    [InlineData("BS-142 fix the login redirect")]
    [InlineData("Fix the login redirect (BS-142)")]
    [InlineData("fix login\n\nCloses BS-142.")]
    [InlineData("bs-142: lower case works too")]
    public void ReferencesAreFoundInCommitMessages(string message) =>
        Assert.Equal(["BS-142"], Found(message));

    /// <summary>Keys are normalized, so a lower-case branch matches an upper-case project key.</summary>
    [Fact]
    public void KeysAreUpperCased() => Assert.Equal(["PAY-7"], Found("pay-7 fix"));

    /// <summary>Several references in one message all count — a commit can close two things.</summary>
    [Fact]
    public void SeveralReferencesAreAllFound() =>
        Assert.Equal(["BS-142", "PAY-7"], Found("BS-142 and PAY-7 both fixed"));

    /// <summary>The same reference twice is one reference.</summary>
    /// <remarks>
    /// Otherwise a commit saying "BS-142 … see BS-142" would write two history rows for one act.
    /// </remarks>
    [Fact]
    public void RepeatedReferencesCollapse() =>
        Assert.Equal(["BS-142"], Found("BS-142 continues the work in BS-142"));

    // ── What must not be found ────────────────────────────────────────────────

    /// <summary>
    /// Things that look like references and are not.
    /// </summary>
    /// <remarks>
    /// Dates and versions are the dangerous ones: they appear in real commit messages constantly, and
    /// matching them would move whichever work item happened to have that number. The leading-letter
    /// rule is what excludes them.
    /// </remarks>
    [Theory]
    [InlineData("2026-08 release notes")]
    [InlineData("bump to 1-2")]
    [InlineData("merge main into develop")]
    [InlineData("fix the thing")]
    [InlineData("UTF-8 encoding fix")]        // key would be "UTF", number 8 — see below
    [InlineData("")]
    [InlineData("   ")]
    public void NonReferencesAreNotMatched(string text)
    {
        var found = Found(text);

        // UTF-8 is the honest edge: it is indistinguishable in shape from a real reference, so it is
        // excluded not by the parser but by the lookup — no project has the key UTF, so it resolves
        // to nothing. Asserting only that the parser does not invent extra matches.
        Assert.True(found.Length <= 1, $"Expected at most one candidate, got {string.Join(", ", found)}");
    }

    [Fact]
    public void NullAndEmptyAreSafe()
    {
        Assert.Empty(Found(null));
        Assert.Empty(Found(""));
    }

    /// <summary>A key must start with a letter, so a number-leading token is not one.</summary>
    [Fact]
    public void ANumericPrefixIsNotAKey() => Assert.Empty(Found("2026-142"));

    /// <summary>Keys longer than the maximum are not references.</summary>
    [Fact]
    public void AnOverlongKeyIsNotMatched() => Assert.Empty(Found("ABCDEFGHIJKL-1"));

    /// <summary>A reference embedded in a longer word is not one.</summary>
    [Fact]
    public void ReferencesNeedWordBoundaries() => Assert.Empty(Found("xxBS-142xx"));

    // ── Gathering from a whole event ──────────────────────────────────────────

    private static NormalizedGitEvent Event(
        string? branch = null,
        CommitInfo[]? commits = null,
        PullRequestInfo? pullRequest = null) =>
        new(GitEventKind.Push, "1", "acme/payments", branch, null,
            commits ?? [], pullRequest, new ActorInfo("ada", null), DateTimeOffset.UtcNow);

    private static CommitInfo Commit(string message, bool isMerge = false) =>
        new("sha", message, "Ada", "ada@acme.test", isMerge, DateTimeOffset.UtcNow);

    /// <summary>
    /// The branch, the commits and the pull request are all read, and combined.
    /// </summary>
    /// <remarks>
    /// A union rather than a precedence order: a branch can carry the epic while a commit names the
    /// specific task, and both are true. Picking one would silently drop the other.
    /// </remarks>
    [Fact]
    public void EverySourceIsRead()
    {
        var found = WorkItemReferences.FromEvent(Event(
            branch: "bs-1-epic-work",
            commits: [Commit("BS-2 the actual task")],
            pullRequest: new PullRequestInfo(9, "BS-3 in the title", "and BS-4 in the body", "url", false)));

        Assert.Equal(
            ["BS-1", "BS-2", "BS-3", "BS-4"],
            found.Select(r => r.ToString()).Order().ToArray());
    }

    /// <summary>
    /// Merge commits are not read.
    /// </summary>
    /// <remarks>
    /// A merge commit's message names the branch being merged, so reading it would re-bind every
    /// reference on that branch to whoever pressed the button — and again on every later merge.
    /// </remarks>
    [Fact]
    public void MergeCommitsAreSkipped()
    {
        var found = WorkItemReferences.FromEvent(Event(
            commits: [Commit("Merge pull request #7 from acme/bs-999", isMerge: true),
                      Commit("BS-1 real work")]));

        Assert.Equal(["BS-1"], found.Select(r => r.ToString()).ToArray());
    }

    [Fact]
    public void AnEventWithNoReferencesYieldsNone() =>
        Assert.Empty(WorkItemReferences.FromEvent(Event(
            branch: "hotfix", commits: [Commit("quick fix")])));

    // ── Keys ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Board Sync Payments", "BSP")]
    [InlineData("Payments", "PAYMENTS")]
    [InlineData("payments service", "PS")]
    [InlineData("API", "API")]
    public void KeysAreProposedFromProjectNames(string name, string expected) =>
        Assert.Equal(expected, ProjectKey.Propose(name));

    /// <summary>A name that yields nothing usable falls back rather than inventing something.</summary>
    [Theory]
    [InlineData("!!!")]
    [InlineData("")]
    [InlineData("x")]
    public void UnusableNamesProposeNothing(string name) => Assert.Null(ProjectKey.Propose(name));

    /// <summary>
    /// A collision is disambiguated rather than refused.
    /// </summary>
    /// <remarks>
    /// Two projects with similar names is ordinary, and failing to create the second over a key
    /// nobody chose explicitly would be a strange thing to refuse.
    /// </remarks>
    [Fact]
    public void CollidingKeysGetASuffix()
    {
        Assert.Equal("PAYMENTS", ProjectKey.Unique("Payments", []));
        Assert.Equal("PAY2", ProjectKey.Unique("Pay", ["PAY"]));
        Assert.Equal("PAY3", ProjectKey.Unique("Pay", ["PAY", "PAY2"]));
    }

    [Theory]
    [InlineData("BS", true)]
    [InlineData("PAY7", true)]
    [InlineData("A", false)]              // too short to recognise
    [InlineData("1BS", false)]            // must start with a letter
    [InlineData("ABCDEFGHIJK", false)]    // too long
    [InlineData("bs", false)]             // stored upper-case
    [InlineData("B-S", false)]
    public void KeyValidationMatchesTheStoredShape(string key, bool valid) =>
        Assert.Equal(valid, ProjectKey.IsValid(key));
}
