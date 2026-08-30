using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Tests;

/// <summary>
/// Reading a <c>.env</c> into the process environment.
/// </summary>
/// <remarks>
/// The rule that matters is precedence. Compose passes real environment variables into a container,
/// so a file that could override them would let a stray <c>.env</c> in an image quietly contradict
/// the deployment — which is the failure this must not introduce while fixing the other one.
/// </remarks>
public class EnvironmentFileTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("boardsync-env").FullName;

    private readonly List<string> _touched = [];

    private string Write(string directory, string contents)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, ".env");

        File.WriteAllText(path, contents);

        return path;
    }

    private void Track(params string[] keys) => _touched.AddRange(keys);

    [Fact]
    public void ValuesAreReadIntoTheEnvironment()
    {
        Track("BOARDSYNC_TEST_ONE", "BOARDSYNC_TEST_TWO");

        Write(_root, """
            BOARDSYNC_TEST_ONE=first
            BOARDSYNC_TEST_TWO=second
            """);

        var result = EnvironmentFile.Load(_root);

        Assert.Equal(2, result.Applied);
        Assert.Equal("first", Environment.GetEnvironmentVariable("BOARDSYNC_TEST_ONE"));
        Assert.Equal("second", Environment.GetEnvironmentVariable("BOARDSYNC_TEST_TWO"));
    }

    [Fact]
    public void TheEnvironmentAlwaysWins()
    {
        Track("BOARDSYNC_TEST_PRECEDENCE");

        Environment.SetEnvironmentVariable(
            "BOARDSYNC_TEST_PRECEDENCE", "from the environment");

        Write(_root, "BOARDSYNC_TEST_PRECEDENCE=from the file");

        var result = EnvironmentFile.Load(_root);

        /*
         * The whole reason this is safe in a container. Compose sets real variables; a file could
         * only ever supply what nobody had set, never contradict the deployment.
         */
        Assert.Equal(
            "from the environment",
            Environment.GetEnvironmentVariable("BOARDSYNC_TEST_PRECEDENCE"));

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void AFileAboveTheAppIsFound()
    {
        Track("BOARDSYNC_TEST_PARENT");

        // The two places somebody reasonably puts one: beside the server, or at the repo root.
        Write(_root, "BOARDSYNC_TEST_PARENT=found");

        var nested = Path.Combine(_root, "BoardSync.Api");

        Directory.CreateDirectory(nested);

        var result = EnvironmentFile.Load(nested);

        Assert.Equal("found", Environment.GetEnvironmentVariable("BOARDSYNC_TEST_PARENT"));
        Assert.EndsWith(".env", result.Path);
    }

    [Fact]
    public void CommentsBlankLinesQuotesAndExportsAreHandled()
    {
        Track("BOARDSYNC_TEST_QUOTED", "BOARDSYNC_TEST_EXPORTED", "BOARDSYNC_TEST_EMPTY");

        Write(_root, """
            # a comment

            BOARDSYNC_TEST_QUOTED="a value with spaces"
            export BOARDSYNC_TEST_EXPORTED=exported
            BOARDSYNC_TEST_EMPTY=
            not a pair
            """);

        EnvironmentFile.Load(_root);

        Assert.Equal(
            "a value with spaces",
            Environment.GetEnvironmentVariable("BOARDSYNC_TEST_QUOTED"));

        // `export FOO=bar` is what a shell-minded reader writes; accepting it beats creating a
        // variable called "export BOARDSYNC_TEST_EXPORTED".
        Assert.Equal(
            "exported",
            Environment.GetEnvironmentVariable("BOARDSYNC_TEST_EXPORTED"));

        // An empty value is a real answer — "set but blank" is how a key is deliberately disabled.
        Assert.Equal("", Environment.GetEnvironmentVariable("BOARDSYNC_TEST_EMPTY"));
    }

    [Fact]
    public void NoFileIsNotAFailure()
    {
        var empty = Directory.CreateTempSubdirectory("boardsync-env-none").FullName;

        var result = EnvironmentFile.Load(empty);

        Assert.Null(result.Path);
        Assert.Equal(0, result.Applied);

        Directory.Delete(empty, recursive: true);
    }

    public void Dispose()
    {
        foreach (var key in _touched)
            Environment.SetEnvironmentVariable(key, null);

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
