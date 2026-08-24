using System.Text.RegularExpressions;

namespace BoardSync.Api.Modules.OrgProject.Domain.Helpers;

/// <summary>
/// The short key people type into branch names: the <c>BS</c> in <c>BS-142</c>.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Slug"/>. A slug lives in a URL and wants to be long and descriptive;
/// this is typed by hand, repeatedly, at the moment somebody is creating a branch — so it wants to
/// be short enough that typing it is not a reason to skip it.
/// </remarks>
public static partial class ProjectKey
{
    /// <summary>Shortest key that is still recognisable in a commit log.</summary>
    public const int MinLength = 2;

    /// <summary>
    /// Longest key. Past this the reference stops being shorter than the thing it names, which is the
    /// only reason it exists.
    /// </summary>
    public const int MaxLength = 10;

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPattern { get; }

    /// <summary>Whether a key is well formed. Must start with a letter — see <c>WorkItemReferences</c>.</summary>
    public static bool IsValid(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ValidPattern.IsMatch(key);

    /// <summary>
    /// Proposes a key from a project's name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initials when the name has several words — "Board Sync Payments" becomes <c>BSP</c>, which is
    /// what a person would have chosen. A single word contributes its first letters instead.
    /// </para>
    /// <para>
    /// A proposal, not an answer: it can collide, and the caller resolves that. It also cannot always
    /// succeed — a name made entirely of punctuation or non-Latin script yields nothing usable — in
    /// which case the caller falls back rather than this inventing something meaningless.
    /// </para>
    /// </remarks>
    public static string? Propose(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var words = name
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string([.. w.Where(char.IsAsciiLetterOrDigit)]))
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0) return null;

        var candidate = words.Count > 1
            ? new string([.. words.Select(w => w[0])])
            : words[0];

        // Trimmed to fit, then leading digits dropped — a key has to start with a letter or it would
        // not be distinguishable from a date in a branch name.
        candidate = new string([.. candidate.Take(MaxLength)]).ToUpperInvariant();
        candidate = candidate.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        return candidate.Length >= MinLength ? candidate : null;
    }

    /// <summary>
    /// A key that is free within the organization, given the ones already taken.
    /// </summary>
    /// <remarks>
    /// Appends a digit on collision — <c>PAY</c>, then <c>PAY2</c> — because two projects with
    /// similar names is the ordinary case, and refusing to create the second over a key nobody chose
    /// explicitly would be a strange thing to fail on.
    /// </remarks>
    public static string Unique(string name, IReadOnlyCollection<string> taken)
    {
        var baseKey = Propose(name) ?? "PRJ";

        if (!taken.Contains(baseKey)) return baseKey;

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var trimmed = baseKey.Length + suffix.ToString().Length > MaxLength
                ? baseKey[..(MaxLength - suffix.ToString().Length)]
                : baseKey;

            var candidate = $"{trimmed}{suffix}";

            if (!taken.Contains(candidate)) return candidate;
        }

        // A thousand projects whose names all reduce to the same initials. Unreachable in practice,
        // and a caller that hits it deserves a real error rather than a silent collision.
        throw new InvalidOperationException(
            $"Could not derive a free project key from '{name}'. Supply one explicitly.");
    }
}
