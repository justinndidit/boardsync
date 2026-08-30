using System.Text.RegularExpressions;

namespace BoardSync.Api.Modules.Search.Domain;

/// <summary>
/// What a search box's contents might mean.
/// </summary>
/// <remarks>
/// Its own type so the rules are testable without a database. The one rule here decides whether a
/// term is a work item reference, and getting it wrong does not fail — it quietly returns somebody
/// else's card, which is the kind of bug that survives a long time.
/// </remarks>
public static partial class SearchTerm
{
    /// <summary>
    /// The numeric half of anything that looks like a work item reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepts <c>BS-142</c>, <c>BS142</c>, <c>bs 142</c> and a bare <c>142</c>. Null for anything
    /// else, which is the common case — most searches are words.
    /// </para>
    /// <para>
    /// <b>The prefix is bounded and lazy, and both matter.</b> Unbounded, any long alphanumeric
    /// string ending in a digit parsed as a reference: a search for <c>zzz4f2a…b1</c> came back as
    /// "work item 1" and returned an unrelated card, which made a test that searched for a random
    /// term fail about six times in a hundred. Greedy, the prefix ate the digits it was looking
    /// for — <c>BS142</c> parsed as <c>2</c>, because <c>S14</c> went into the prefix.
    /// </para>
    /// </remarks>
    public static int? ReferenceNumber(string term)
    {
        var match = ReferencePattern().Match(term.Trim());

        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// A reference: an optional key, an optional separator, a number.
    /// </summary>
    /// <remarks>
    /// <c>{0,9}</c> after the leading letter caps the prefix at ten characters, which is
    /// <c>ProjectKey.MaxLength</c> — written out because <c>[GeneratedRegex]</c> takes a constant.
    /// The cap is mirrored rather than imported: search accepts terms that were never valid keys —
    /// lower case, a stray space — so coupling it to the domain's validator would mean either
    /// loosening that validator or rejecting what people actually type.
    /// </remarks>
    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9]{0,9}?[\s-]*)?(\d{1,9})$")]
    private static partial Regex ReferencePattern();
}
