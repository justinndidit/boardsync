using BoardSync.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Extensions;

public static class DatabaseExtensions
{
    /// <summary>
    /// Arbitrary but fixed key identifying the migration lock. Any value works as long as every
    /// instance uses the same one and nothing else in the database picks it too.
    /// </summary>
    private const long MigrationLockKey = 8_531_907_442_100_311;

    /// <summary>
    /// Applies pending migrations at startup, if this instance is configured to do so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default in Production. Schema changes there belong in the release pipeline as their
    /// own step, where a failure stops the rollout instead of surfacing as replicas that crash-loop
    /// or, worse, come up healthy against a half-migrated schema. Set <c>Database:AutoMigrate</c>
    /// to opt back in — the local production-like compose stack does exactly that, because it has
    /// no separate migration step to run.
    /// </para>
    /// <para>
    /// When it does run, it runs under a Postgres advisory lock. Instances start together during a
    /// rolling deploy, and EF takes no cross-process lock of its own: without this, two of them read
    /// the same empty migration history and both try to create the same tables. The lock is
    /// session-scoped, so an instance that dies mid-migration releases it by disconnecting rather
    /// than wedging every other replica.
    /// </para>
    /// </remarks>
    public static async Task<WebApplication> MigrateDatabaseAsync(this WebApplication app)
    {
        var autoMigrate = app.Configuration.GetValue(
            "Database:AutoMigrate",
            defaultValue: !app.Environment.IsProduction());

        if (!autoMigrate)
        {
            app.Logger.LogInformation(
                "Startup auto-migration is disabled. Apply migrations as a release step " +
                "(dotnet ef database update), or set Database:AutoMigrate to enable it here.");
            return app;
        }

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<BoardSyncDbContext>>();

        try
        {
            // The lock is held on a session, so the connection has to stay open across both the
            // lock and the migration. Opening it here makes EF reuse this one rather than taking a
            // fresh connection from the pool that would not hold the lock.
            await context.Database.OpenConnectionAsync();

            try
            {
                logger.LogInformation("Acquiring migration lock...");
                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_lock({0})", MigrationLockKey);

                // Re-read after acquiring: whoever held the lock first may have just applied
                // everything, in which case there is nothing left to do.
                var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

                if (pendingMigrations.Count > 0)
                {
                    logger.LogInformation("Applying {Count} pending migrations: {Migrations}",
                        pendingMigrations.Count, string.Join(", ", pendingMigrations));

                    await context.Database.MigrateAsync();
                    logger.LogInformation("Database migration completed successfully");
                }
                else
                {
                    logger.LogInformation("Database is up to date, no migrations needed");
                }
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_unlock({0})", MigrationLockKey);
                await context.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while migrating the database");

            // Fail fast anywhere the schema is expected to be authoritative.
            //
            // Production is obvious: an instance serving traffic against a schema it could not
            // migrate is worse than one that never starts. Testing was added after a broken
            // migration was swallowed here and surfaced instead as twenty unrelated integration
            // failures reporting "a database error occurred" — the schema was half applied, and
            // nothing said so. A test run on a schema that did not build is not a test run.
            if (app.Environment.IsProduction()
                || app.Environment.IsEnvironment("Testing"))
            {
                throw;
            }

            logger.LogWarning("Database migration failed in development environment, continuing...");
        }

        return app;
    }
}
