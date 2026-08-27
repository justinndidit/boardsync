using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// The real API, booted against a real Postgres, for one test class collection.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests in this project cover the rules — the evaluator, the vocabulary, the state
/// machine — and they cover them well because those pieces are pure. What they cannot see is
/// everything that only exists once the pieces are wired: whether a query translates, whether a
/// column is actually written, whether an endpoint's guard resolves the scope it names, whether an
/// event reaches its handler. Three of the defects found in the August audit lived exactly there,
/// and one of them — a documented, indexed, migrated column that nothing ever wrote — had been
/// shipping silently because no test had ever written a work item and read it back.
/// </para>
/// <para>
/// So this boots <c>Program</c> itself rather than a hand-assembled service collection. The
/// pipeline under test is the one that runs in production: the same middleware order, the same
/// authorization filter, the same JSON options, the same migrations applied on startup.
/// </para>
/// <para>
/// <b>Postgres, not an in-memory provider.</b> Half of what these tests exist to check is SQL —
/// <c>= ANY</c> translation, <c>FOR UPDATE SKIP LOCKED</c>, <c>xmin</c> as a row version, check
/// constraints, partial unique indexes, <c>LISTEN/NOTIFY</c>. An in-memory provider would answer
/// every one of those questions wrongly and cheerfully.
/// </para>
/// <para>
/// Redis is deliberately left unconfigured: the API falls back to in-process caching and per-process
/// rate limits, which is correct for a single instance and removes a container from the critical
/// path. Anything specifically about the distributed cache needs its own fixture.
/// </para>
/// </remarks>
public sealed class BoardSyncApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("boardsync_test")
        .WithUsername("postgres")
        .WithPassword("postgres")

        // Each run gets a clean database, so tests never inherit another run's rows. Reuse would be
        // faster and would make failures depend on what ran before them.
        .WithCleanUp(true)
        .Build();

    // Implemented explicitly: xUnit's IAsyncLifetime returns Task, while WebApplicationFactory
    // already defines a virtual ValueTask DisposeAsync, and the two signatures cannot coexist
    // implicitly on one type.
    Task IAsyncLifetime.InitializeAsync() => _postgres.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => ShutDownAsync();

    private async Task ShutDownAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // Apply test settings through the host configuration so the top-level Program and all
        // services use the Testcontainers database and matching authentication settings.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),

                    // Explicitly cleared rather than merely absent: appsettings.Development.json points
                    // at a developer's local Redis, and a test run must not reach for it.
                    ["ConnectionStrings:Redis"] = "",

                    // Applied on startup, so the schema under test is whatever the migrations produce —
                    // which also means a broken migration fails the suite rather than production.
                    ["Database:AutoMigrate"] = "true",

                    ["JwtSettings:Secret"] = "integration-tests-signing-key-at-least-32-chars",
                    ["JwtSettings:Issuer"] = "BoardSync.Api.Tests",
                    ["JwtSettings:Audience"] = "BoardSync.Client.Tests",

                    // Off, or a test class making a few dozen calls trips the limiter and fails for a
                    // reason that has nothing to do with what it was checking.
                    ["RateLimiting:Enabled"] = "false",

                    // Registration would otherwise leave every account inactive and unable to sign in.
                    ["SecuritySettings:RequireEmailConfirmation"] = "false",

                    // Drained aggressively so a latency assertion measures the NOTIFY path rather than
                    // the polling fallback. The default 5s would let a broken wake-up look fine.
                    ["Outbox:PollIntervalSeconds"] = "30",

                    ["Telemetry:OtlpEndpoint"] = ""
            }));

        builder.UseSetting("JwtSettings:Secret", "integration-tests-signing-key-at-least-32-chars");
        builder.UseSetting("JwtSettings:Issuer", "BoardSync.Api.Tests");
        builder.UseSetting("JwtSettings:Audience", "BoardSync.Client.Tests");

        // Warnings and worse. Information-level EF logging prints every statement, which buries an
        // actual failure in thousands of lines of SQL.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }
}

/// <summary>
/// Shares one API host and one database container across every integration test class.
/// </summary>
/// <remarks>
/// Starting Postgres costs a few seconds; paying that per class would make the suite slow enough
/// that people stop running it, which is the failure mode that matters most. Tests therefore share
/// a database and must not assume it is empty — each creates its own organization and works inside
/// it, which is also closer to how the system really runs.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<BoardSyncApiFactory>
{
    public const string Name = "BoardSync API";
}
