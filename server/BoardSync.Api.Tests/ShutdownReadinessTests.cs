using Microsoft.Extensions.Hosting;
using BoardSync.Api.Shared.Kernel.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BoardSync.Api.Tests;

/// <summary>
/// Readiness during shutdown.
/// </summary>
/// <remarks>
/// <para>
/// Liveness and readiness differ only while an instance is shutting down, and that window is the
/// entire reason the second endpoint exists: a draining process is still alive, so a probe that
/// asks "alive?" keeps a load balancer sending it traffic right up until the socket closes.
/// </para>
/// <para>
/// Asserted here rather than against a running host because the window is too short to catch from
/// outside — an idle instance goes from signalled to stopped in about a quarter of a second, so a
/// poll that tries to observe it mostly misses. What decides the behaviour is this check, and it
/// can be asked directly.
/// </para>
/// </remarks>
public class ShutdownReadinessTests
{
    /// <summary>An application lifetime whose stopping signal the test controls.</summary>
    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private static Task<HealthCheckResult> Ask(IHostApplicationLifetime lifetime) =>
        new ShutdownReadinessCheck(lifetime)
            .CheckHealthAsync(new HealthCheckContext(), default);

    [Fact]
    public async Task ARunningInstanceIsReady()
    {
        using var lifetime = new FakeLifetime();

        Assert.Equal(HealthStatus.Healthy, (await Ask(lifetime)).Status);
    }

    /// <summary>
    /// The one that matters: unready as soon as shutdown is signalled.
    /// </summary>
    /// <remarks>
    /// <c>ApplicationStopping</c> fires before the server stops accepting, which is what gives an
    /// orchestrator the chance to take this instance out of rotation while it can still finish the
    /// requests it is already holding.
    /// </remarks>
    [Fact]
    public async Task AnInstanceThatHasBegunShuttingDownIsNotReady()
    {
        using var lifetime = new FakeLifetime();

        lifetime.StopApplication();

        Assert.Equal(HealthStatus.Unhealthy, (await Ask(lifetime)).Status);
    }

    /// <summary>
    /// The check is tagged for readiness, which is what keeps it out of liveness.
    /// </summary>
    /// <remarks>
    /// Program.cs selects on this tag in both directions — <c>/healthz</c> excludes it,
    /// <c>/healthz/ready</c> requires it. Were the tag to drift, liveness would start failing
    /// during a shutdown and invite a restart of something already on its way out.
    /// </remarks>
    [Fact]
    public void TheReadinessTagIsTheOneProgramSelectsOn() =>
        Assert.Equal("ready", ShutdownReadinessCheck.ReadyTag);
}
