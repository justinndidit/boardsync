using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BoardSync.Api.Extensions;

public static class TelemetryExtensions
{
    /// <summary>
    /// Npgsql publishes its own spans and metrics under this name — database command durations and,
    /// importantly, the connection pool counters. Pool saturation is the number that tells you
    /// whether the per-instance pool size is right before it turns into request timeouts.
    /// </summary>
    private const string NpgsqlSourceName = "Npgsql";

    /// <summary>
    /// Wires up OpenTelemetry traces and metrics, but only when somewhere to send them is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated deliberately. Registering the SDK makes it a listener on every instrumented
    /// <c>ActivitySource</c>, so spans get built and sampled whether or not anything consumes them;
    /// with no exporter that is pure overhead plus a log full of failed connection attempts to a
    /// collector that isn't there. Unconfigured, this costs nothing and says so once at startup.
    /// </para>
    /// <para>
    /// Set <c>Telemetry:OtlpEndpoint</c>, or the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// environment variable, to turn it on.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddBoardSyncTelemetry(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["Telemetry:OtlpEndpoint"]
                       ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var otlpUri))
            return builder;

        var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "boardsync-api";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)
                ]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes run every few seconds forever and are never the thing being
                    // diagnosed; tracing them buries the requests that matter.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/healthz");
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddSource(NpgsqlSourceName)
                .AddOtlpExporter(options => options.Endpoint = otlpUri))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(NpgsqlSourceName)
                .AddOtlpExporter(options => options.Endpoint = otlpUri));

        return builder;
    }

    /// <summary>
    /// Logs whether telemetry is live, so an operator can tell "no traces because nothing happened"
    /// apart from "no traces because it was never switched on".
    /// </summary>
    public static WebApplication LogTelemetryStatus(this WebApplication app)
    {
        var endpoint = app.Configuration["Telemetry:OtlpEndpoint"]
                       ?? app.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            app.Logger.LogInformation(
                "OpenTelemetry is not configured — set Telemetry:OtlpEndpoint or " +
                "OTEL_EXPORTER_OTLP_ENDPOINT to export traces and metrics.");
        }
        else
        {
            app.Logger.LogInformation("OpenTelemetry exporting traces and metrics to {Endpoint}", endpoint);
        }

        return app;
    }
}
