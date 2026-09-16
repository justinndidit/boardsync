using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BoardSync.Api.Shared.Kernel.Health;

/// <summary>
/// Reports this instance as unready the moment it begins shutting down.
/// </summary>
/// <remarks>
/// <para>
/// Liveness and readiness answer different questions, and one endpoint answering both gets the
/// second one wrong. <c>/healthz</c> asked "is the process alive", and a draining instance kept
/// answering yes right up until Kestrel stopped — so a load balancer went on routing requests to
/// a process that had already been told to go away. Every rolling deploy dropped whatever arrived
/// in that window.
/// </para>
/// <para>
/// This is the readiness half: it flips as soon as <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// fires, which is before the server stops accepting, giving the orchestrator a chance to take the
/// instance out of rotation while it can still serve what it already has.
/// </para>
/// <para>
/// Liveness deliberately does not include this check. An instance that is shutting down is not
/// faulty, and reporting it as such invites a restart of something already on its way out.
/// </para>
/// </remarks>
public sealed class ShutdownReadinessCheck : IHealthCheck
{
    /// <summary>The tag that routes a check to readiness rather than liveness.</summary>
    public const string ReadyTag = "ready";

    private readonly IHostApplicationLifetime _lifetime;

    public ShutdownReadinessCheck(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(
            _lifetime.ApplicationStopping.IsCancellationRequested
                ? HealthCheckResult.Unhealthy("Shutting down; not accepting new traffic.")
                : HealthCheckResult.Healthy("Accepting traffic."));
}
