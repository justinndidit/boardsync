using System.Text.RegularExpressions;

namespace BoardSync.Api.Modules.GitSync.Domain;

/// <summary>
/// A mention of a work item in something a developer typed — <c>BS-142</c>.
/// </summary>
/// <param name="ProjectKey">The project's short key, upper-cased.</param>
/// <param name="Number">The work item's number within that project.</param>
public readonly record struct WorkItemReference(string ProjectKey, int Number)
{
    public override string ToString() => $"{ProjectKey}-{Number}";
}

/// <summary>
/// Finds work item references in branch names, commit messages and pull request text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a human-readable key rather than an id.</b> Nobody types a GUID into a branch name, and a
/// system that asks them to will not be used — which would make the whole self-updating premise
/// theoretical. <c>BS-142</c> is short enough to type once at branch creation and recognisable
/// enough to read in a commit log.
/// </para>
/// <para>
/// <b>Branch name is the primary signal, deliberately.</b> A developer names a branch once, at the
/// moment they are already thinking about which ticket they are on; requiring the reference in every
/// commit relies on discipline at the exact moment nobody has any. Commit and pull request text are
/// read too, so an explicit mention still works — see <see cref="FromEvent"/>.
/// </para>
/// </remarks>
public static partial class WorkItemReferences
{
    /// <summary>
    /// A project key followed by a number: <c>BS-142</c>, <c>PAY-7</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key must start with a letter and run 2–10 characters, which is what keeps this from
    /// matching the things that surround real branch names and commit messages. Without the leading
    /// letter it would match dates (<c>2026-08</c>) and versions; without the length bound it would
    /// match most hyphenated words.
    /// </para>
    /// <para>
    /// Delimited by non-alphanumerics rather than by <c>\b</c>. A word boundary treats <c>_</c> as
    /// part of a word, so <c>BS-142_login</c> — an ordinary branch name — would not have matched,
    /// while <c>xxBS-142xx</c> would have been rejected for the right reason by accident. Requiring
    /// the reference to be surrounded by something that is not a letter or digit gets both right.
    /// </para>
    /// <para>
    /// Case-insensitive in effect because branch names are conventionally lower-case and commit
    /// messages are not; the key is upper-cased before it is used.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]{1,9})-(\d{1,9})(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern { get; }

    /// <summary>Every distinct reference in one piece of text, in the order they appear.</summary>
    public static IReadOnlyList<WorkItemReference> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<WorkItemReference>? found = null;

        foreach (var match in ReferencePattern.EnumerateMatches(text))
        {
            var span = text.AsSpan(match.Index, match.Length);
            var separator = span.LastIndexOf('-');

            // Bounded by the pattern to nine digits, so this cannot overflow — but a malformed
            // capture must not throw on a path fed by arbitrary commit text.
            if (!int.TryParse(span[(separator + 1)..], out var number) || number <= 0) continue;

            var reference = new WorkItemReference(
                span[..separator].ToString().ToUpperInvariant(), number);

            found ??= [];

            // Distinct: "BS-142 ... see BS-142" is one reference mentioned twice, and binding it
            // twice would write two history rows for one act.
            if (!found.Contains(reference)) found.Add(reference);
        }

        return found ?? (IReadOnlyList<WorkItemReference>)[];
    }

    /// <summary>
    /// Every work item a git event refers to, from all the places a developer might have said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union of the branch name, every non-merge commit message, and the pull request's title and
    /// body. A union rather than a precedence order because these are not competing answers to one
    /// question — a branch can carry the epic while a commit names the specific task, and both are
    /// true.
    /// </para>
    /// <para>
    /// <b>Merge commits are skipped.</b> A merge is not authorship: its message names the branch
    /// being merged, so reading it would re-bind every reference on that branch to whoever pressed
    /// the button, and would do so again on every subsequent merge.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WorkItemReference> FromEvent(Providers.NormalizedGitEvent gitEvent)
    {
        var found = new List<WorkItemReference>();

        Add(found, Parse(gitEvent.BranchName));

        foreach (var commit in gitEvent.Commits)
        {
            if (commit.IsMerge) continue;
            Add(found, Parse(commit.Message));
        }

        if (gitEvent.PullRequest is { } pullRequest)
        {
            Add(found, Parse(pullRequest.Title));
            Add(found, Parse(pullRequest.Body));
        }

        return found;
    }

    private static void Add(List<WorkItemReference> into, IReadOnlyList<WorkItemReference> found)
    {
        foreach (var reference in found)
            if (!into.Contains(reference))
                into.Add(reference);
    }
}
