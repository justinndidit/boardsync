namespace BoardSync.Api.Shared.Kernel;

/// <summary>
/// Reads a <c>.env</c> file into the process environment.
/// </summary>
/// <remarks>
/// <para>
/// .NET does not read <c>.env</c> files. The one in this repository existed only for
/// <c>docker-compose</c> variable substitution, so a developer who put a key in it and ran the API
/// locally got a process that had never heard of the file — with no error, because a missing key is
/// a legitimate state that disables the feature quietly.
/// </para>
/// <para>
/// <b>An existing environment variable always wins.</b> That is what keeps this safe in a container:
/// Compose passes real variables in, and a <c>.env</c> that somehow reached the image could then
/// only supply values nobody had set, never override the deployment's own. It also means the file
/// cannot silently contradict what an operator exported by hand.
/// </para>
/// <para>
/// The search walks up from the content root, so both <c>server/.env</c> and a root <c>.env</c>
/// work — the two places somebody reasonably puts one.
/// </para>
/// </remarks>
public static class EnvironmentFile
{
    /// <summary>How far up to look. Repository root is two or three levels above the app.</summary>
    private const int MaxDepth = 4;

    /// <summary>What was loaded, for a startup line that makes this visible rather than magic.</summary>
    /// <param name="Path">The file read, or null when none was found.</param>
    /// <param name="Applied">Variables set. Excludes any the environment already defined.</param>
    /// <param name="Skipped">Variables the environment already defined, and which therefore won.</param>
    public readonly record struct Result(string? Path, int Applied, int Skipped);

    public static Result Load(string contentRoot)
    {
        var file = Find(contentRoot);

        if (file is null) return new Result(null, 0, 0);

        var applied = 0;
        var skipped = 0;

        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#')) continue;

            var split = line.IndexOf('=');

            if (split <= 0) continue;

            var key = line[..split].Trim();

            // `export FOO=bar` is what a shell-minded reader writes; accept it rather than
            // silently creating a variable called "export FOO".
            if (key.StartsWith("export ", StringComparison.Ordinal))
                key = key["export ".Length..].Trim();

            if (key.Length == 0) continue;

            if (Environment.GetEnvironmentVariable(key) is not null)
            {
                skipped++;
                continue;
            }

            Environment.SetEnvironmentVariable(key, Unquote(line[(split + 1)..].Trim()));
            applied++;
        }

        return new Result(file, applied, skipped);
    }

    /// <summary>The nearest <c>.env</c> at or above <paramref name="start"/>.</summary>
    private static string? Find(string start)
    {
        var directory = new DirectoryInfo(start);

        for (var depth = 0; depth < MaxDepth && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, ".env");

            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Strips one matching pair of surrounding quotes.
    /// </summary>
    /// <remarks>
    /// Only a matching pair, and only the outermost. A value that genuinely begins and ends with a
    /// quote character is vanishingly rare next to somebody quoting a key that contains a <c>#</c>.
    /// </remarks>
    private static string Unquote(string value) =>
        value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"')
            || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
