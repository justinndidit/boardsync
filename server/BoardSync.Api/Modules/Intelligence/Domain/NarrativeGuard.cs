using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardSync.Api.Modules.Intelligence.Domain;

/// <summary>
/// Checks that a narrative only states figures it was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the load-bearing part of the module.</b> <c>build_context.md</c> §8.3 argues that a
/// model asked to both compute and narrate produces plausible numbers nobody downstream can tell
/// from computed ones. Separating the modules answers the first half — <c>Reporting</c> computes,
/// <c>Intelligence</c> narrates — and this answers the second: every number in the prose has to
/// appear in the report it was handed, or the sentence containing it does not ship.
/// </para>
/// <para>
/// Pure, and tested without an API key, which is the point. Whether the model writes well is a
/// judgement; whether it invented a number is a fact, and facts are checkable.
/// </para>
/// <para>
/// <b>What this does not claim.</b> It catches invented figures, not wrong emphasis, false
/// causation, or a true number used to support an untrue sentence. "Velocity rose because the team
/// worked harder" cites nothing and passes. This is a floor, not a guarantee, and it is the floor
/// worth having because a fabricated number is the failure that survives review.
/// </para>
/// </remarks>
public static partial class NarrativeGuard
{
    /// <summary>
    /// Numbers as they appear in prose: <c>40</c>, <c>2.5</c>, <c>91%</c>, <c>1,200</c>.
    /// </summary>
    /// <remarks>
    /// Ordinals and small counting words are not matched — "the first sprint" and "three items" are
    /// not claims about the data in the way "40 points" is, and treating them as citations would
    /// reject ordinary English.
    /// </remarks>
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();

    /// <summary>
    /// How close a stated figure has to be to a real one.
    /// </summary>
    /// <remarks>
    /// Not exact equality: a report carrying 2.5 hours may reasonably be narrated as "about 3
    /// hours", and rejecting that would push the prose toward reciting decimals at people. The
    /// tolerance is tight enough that a different figure cannot pass as a rounding of a real one.
    /// </remarks>
    private const double RelativeTolerance = 0.02;

    /// <summary>One sentence that stated a figure the report does not contain.</summary>
    /// <param name="Sentence">The sentence, as written.</param>
    /// <param name="Figure">The number in it that nothing supports.</param>
    public readonly record struct Unsupported(string Sentence, string Figure);

    /// <summary>
    /// The sentences whose numbers are not in <paramref name="supported"/>.
    /// </summary>
    /// <param name="narrative">The prose to check.</param>
    /// <param name="supported">
    /// Every figure the report contains. Supply generously — a number omitted here is a true
    /// sentence rejected, which teaches people to ignore the guard.
    /// </param>
    public static IReadOnlyList<Unsupported> UnsupportedClaims(
        string narrative,
        IReadOnlyCollection<double> supported)
    {
        if (string.IsNullOrWhiteSpace(narrative)) return [];

        var findings = new List<Unsupported>();

        foreach (var sentence in Sentences(narrative))
        {
            /*
             * Identifiers are not quantities. "PAY-11 shipped" contains no claim about eleven of
             * anything, and reading it as one would flag every correctly-cited work item as an
             * invented figure. References are checked separately, and by name, in
             * `UnsupportedReferences`.
             */
            var quantities = References().Replace(sentence, " ");

            foreach (Match match in Numbers().Matches(quantities))
            {
                if (!double.TryParse(
                        match.Value.Replace(",", ""),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var stated))
                {
                    continue;
                }

                if (supported.Any(known => Matches(stated, known))) continue;

                findings.Add(new Unsupported(sentence.Trim(), match.Value));
            }
        }

        return findings;
    }

    /// <summary>Whether a narrative states nothing the report does not.</summary>
    /// <summary>
    /// Work item references the prose named that were never handed to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The narrator can name work now, which is what makes its reports readable and also what gives
    /// it a second way to be wrong. A fabricated <c>PAY-91</c> is more damaging than a fabricated
    /// number: a reader can check a figure against the table beside it, and has no way at all to
    /// know that an item does not exist.
    /// </para>
    /// <para>
    /// Matched case-insensitively on the whole reference, and only against what the model was given.
    /// A reference that is real but belongs to another sprint is still unsupported here — this
    /// report is about this sprint, and naming work from elsewhere would be as misleading as
    /// inventing it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Unsupported> UnsupportedReferences(
        string narrative,
        IReadOnlyCollection<string> known)
    {
        if (string.IsNullOrWhiteSpace(narrative)) return [];

        var allowed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        var findings = new List<Unsupported>();

        foreach (var sentence in Sentences(narrative))
        {
            foreach (Match match in References().Matches(sentence))
            {
                if (allowed.Contains(match.Value)) continue;

                findings.Add(new Unsupported(sentence.Trim(), match.Value));
            }
        }

        return findings;
    }

    /// <summary>
    /// A work item reference: a project key, a dash, a number.
    /// </summary>
    /// <remarks>
    /// The same shape <c>WorkItemReference</c> parses out of branch names — two to ten leading
    /// letters so it does not match a date or a version number.
    /// </remarks>
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9]{1,9}-\d+\b")]
    private static partial Regex References();

    public static bool IsGrounded(
        string narrative,
        IReadOnlyCollection<double> supported) =>
        UnsupportedClaims(narrative, supported).Count == 0;

    private static bool Matches(double stated, double known)
    {
        if (stated == known) return true;

        // A percentage the report holds as a ratio, and the reverse.
        if (Math.Abs(stated - known * 100) < 0.5) return true;

        var scale = Math.Max(Math.Abs(known), 1);

        return Math.Abs(stated - known) <= scale * RelativeTolerance;
    }

    /// <remarks>
    /// Split on sentence-ending punctuation followed by space. Deliberately simple: the split only
    /// decides how much context a rejected figure is reported with, so getting an abbreviation
    /// wrong costs a slightly odd message rather than a wrong verdict.
    /// </remarks>
    private static IEnumerable<string> Sentences(string text) =>
        SentenceBreak().Split(text).Where(s => !string.IsNullOrWhiteSpace(s));

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();
}
