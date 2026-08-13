using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Kernel.Configuration;
using BoardSync.Api.Shared.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace BoardSync.Api.Extensions;

public static class RealtimeExtensions
{
    /// <summary>Where clients connect.</summary>
    public const string HubPath = "/hubs/workspace";

    /// <summary>
    /// Registers the real-time hub and its Redis backplane.
    /// </summary>
    /// <remarks>
    /// The backplane is what makes this work with more than one instance: a client connected to
    /// instance A must receive a change written on instance B, and without it each instance can only
    /// reach its own connections. It is registered only when Redis is configured — single-instance
    /// development works without it, and the startup log says which mode is in effect.
    /// </remarks>
    public static WebApplicationBuilder AddBoardSyncRealtime(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RealtimeSettings>(builder.Configuration.GetSection("Realtime"));

        var signalR = builder.Services.AddSignalR(options =>
        {
            // Surfacing exception text to clients would leak internals; the hub returns explicit
            // results for the failures a client is meant to handle.
            options.EnableDetailedErrors = builder.Environment.IsDevelopment();
        });

        var redisConnection = builder.Configuration.GetConnectionString("Redis");

        if (!string.IsNullOrWhiteSpace(redisConnection))
            signalR.AddStackExchangeRedis(redisConnection, options => options.Configuration.ChannelPrefix =
                StackExchange.Redis.RedisChannel.Literal("boardsync-signalr"));

        // Presence is Redis-only; without it the hub simply reports nobody present rather than
        // pretending to track something it cannot share between instances.
        if (!string.IsNullOrWhiteSpace(redisConnection))
            builder.Services.AddScoped<IPresenceTracker, PresenceTracker>();

        // Board snapshot caching needs a shared generation counter; without Redis the board reads
        // through to the database rather than caching something it cannot invalidate.
        if (!string.IsNullOrWhiteSpace(redisConnection))
            builder.Services.AddScoped<IBoardCacheVersion, BoardCacheVersion>();

        builder.Services.AddScoped<ITopicAuthorizer, TopicAuthorizer>();
        builder.Services.AddScoped<IRealtimeReplay, RealtimeReplay>();
        builder.Services.AddScoped<IRealtimeNotifier, SignalRNotifier>();

        return builder;
    }

    /// <summary>
    /// Maps the hub, unless real-time is switched off.
    /// </summary>
    public static WebApplication MapBoardSyncRealtime(this WebApplication app)
    {
        var enabled = app.Configuration.GetValue("Realtime:Enabled", true);

        if (!enabled)
        {
            app.Logger.LogInformation("Real-time hub is disabled; clients will not be able to connect.");
            return app;
        }

        app.MapHub<WorkspaceHub>(HubPath);

        var hasBackplane = !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Redis"));

        if (hasBackplane)
        {
            app.Logger.LogInformation("Real-time hub mapped at {Path} with a Redis backplane.", HubPath);
        }
        else
        {
            app.Logger.LogWarning(
                "Real-time hub mapped at {Path} with NO backplane — a message written on one " +
                "instance will not reach clients connected to another. Single instance only.",
                HubPath);
        }

        return app;
    }

    /// <summary>
    /// Lets the hub read its bearer token from the query string.
    /// </summary>
    /// <remarks>
    /// Browsers cannot set headers on a WebSocket handshake, so SignalR sends the token as
    /// <c>?access_token=</c> instead. This is scoped to the hub path only — accepting query-string
    /// tokens on ordinary endpoints would put credentials into every access log and referrer header
    /// that touches a URL.
    /// </remarks>
    public static void ReadTokenFromQueryStringForHub(JwtBearerOptions options)
    {
        options.Events ??= new JwtBearerEvents();

        var existing = options.Events.OnMessageReceived;

        options.Events.OnMessageReceived = async context =>
        {
            if (existing is not null)
                await existing(context);

            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments(HubPath))
                context.Token = accessToken;
        };
    }
}
