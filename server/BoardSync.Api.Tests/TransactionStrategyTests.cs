using System.Reflection;

namespace BoardSync.Api.Tests;

/// <summary>
/// Every database transaction runs through the retrying execution strategy.
/// </summary>
/// <remarks>
/// <para>
/// The connection is configured with <c>EnableRetryOnFailure</c>, and
/// <c>NpgsqlRetryingExecutionStrategy</c> refuses to retry a transaction the caller opened itself:
/// <c>BeginTransactionAsync</c> throws <see cref="InvalidOperationException"/> before a single row
/// is written.
/// </para>
/// <para>
/// It is a bad failure to own. It cannot happen in a unit test, because no unit test has a
/// connection; it cannot happen without the retry setting, so it never appears in a smaller
/// harness; and the middleware turns it into a flat "Invalid operation" with a 400, which reads
/// like a rejected request rather than code that cannot run. Accepting a decomposition failed this
/// way for as long as the feature existed, and was only found when somebody first had a key to try
/// it with.
/// </para>
/// <para>
/// Checked on the source rather than by reflection: which strategy a method used is not something
/// a compiled assembly records.
/// </para>
/// </remarks>
public class TransactionStrategyTests
{
    [Fact]
    public void EveryTransactionIsOpenedInsideAnExecutionStrategy()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ApiSource(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);

            if (!source.Contains("BeginTransactionAsync")) continue;

            if (source.Contains("CreateExecutionStrategy")) continue;

            offenders.Add(Path.GetRelativePath(ApiSource(), file));
        }

        Assert.True(offenders.Count == 0,
            "These open a transaction without the retrying execution strategy, which throws "
            + "InvalidOperationException at runtime rather than failing here or in review:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nWrap it: var strategy = context.Database.CreateExecutionStrategy(); "
            + "await strategy.ExecuteAsync(async () => { ... });");
    }

    /// <summary>
    /// The API's source directory, found by walking up to the solution rather than by a fixed
    /// number of <c>..</c> segments — which is a thing that silently stops matching anything.
    /// </summary>
    private static string ApiSource()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null)
        {
            var api = Path.Combine(directory.FullName, "BoardSync.Api");

            if (Directory.Exists(api) && File.Exists(Path.Combine(api, "Program.cs")))
                return api;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the BoardSync.Api source directory from the test assembly.");
    }
}
