using Microsoft.Extensions.Caching.Hybrid;

namespace BoardSync.Api.Extensions;

public static class CachingExtensions
{
    /// <summary>
    /// Registers <c>HybridCache</c> — in-process L1 always, Redis L2 when a connection string is
    /// configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without Redis this still works: L1 alone is correct for a single instance, just not shared.
    /// That is deliberate so a developer can run the API with nothing but Postgres. It is also why
    /// nothing cached here may be authoritative — with several instances and no L2, each holds its
    /// own copy and they expire independently.
    /// </para>
    /// <para>
    /// The default entry lifetime is deliberately short. Everything cached is derived data whose
    /// source of truth is Postgres, and a stale permission is a security problem rather than a
    /// performance one, so entries are also invalidated explicitly on write — see
    /// <c>CachingRbacService</c>.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddBoardSyncCaching(this WebApplicationBuilder builder)
    {
        var redisConnection = builder.Configuration.GetConnectionString("Redis");

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "boardsync:";
            });
        }

        builder.Services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5),      // L2, shared
                LocalCacheExpiration = TimeSpan.FromSeconds(60)  // L1, per instance
            };

            // Guard rail, not a target: anything approaching this is the wrong thing to be caching.
            options.MaximumPayloadBytes = 1024 * 512;
        });

        return builder;
    }

    /// <summary>
    /// Logs whether the distributed tier is live, so "the cache is not shared across instances" is
    /// visible at startup rather than inferred from odd behaviour later.
    /// </summary>
    public static WebApplication LogCacheStatus(this WebApplication app)
    {
        var redisConnection = app.Configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            app.Logger.LogWarning(
                "No Redis connection configured — caching is in-process only. Fine for a single " +
                "instance; set ConnectionStrings:Redis before running more than one.");
        }
        else
        {
            app.Logger.LogInformation("Distributed cache backed by Redis at {Connection}.", redisConnection);
        }

        return app;
    }
}
