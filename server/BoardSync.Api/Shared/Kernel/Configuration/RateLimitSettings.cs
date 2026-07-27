namespace BoardSync.Api.Shared.Kernel.Configuration;

/// <summary>
/// Rate limiting configuration, bound from the "RateLimiting" configuration section.
///
/// Every window is applied <b>per caller</b> — partitioned by authenticated user ID when the
/// request carries one, otherwise by client IP. A limit is never shared across unrelated
/// callers, so one user exhausting their budget cannot lock anybody else out.
/// </summary>
public class RateLimitSettings
{
    /// <summary>Set to false to disable rate limiting entirely (useful for local development and tests).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>General API traffic.</summary>
    public RateLimitPolicySettings Api { get; set; } = new(permitLimit: 300, windowSeconds: 60, queueLimit: 10);

    /// <summary>Authentication endpoints: login, register, refresh, logout.</summary>
    public RateLimitPolicySettings Auth { get; set; } = new(permitLimit: 30, windowSeconds: 60, queueLimit: 2);

    /// <summary>Password recovery endpoints: forgot-password and reset-password.</summary>
    public RateLimitPolicySettings Password { get; set; } = new(permitLimit: 5, windowSeconds: 300, queueLimit: 0);
}

/// <summary>A single fixed-window policy.</summary>
public class RateLimitPolicySettings
{
    public RateLimitPolicySettings() { }

    public RateLimitPolicySettings(int permitLimit, int windowSeconds, int queueLimit)
    {
        PermitLimit = permitLimit;
        WindowSeconds = windowSeconds;
        QueueLimit = queueLimit;
    }

    /// <summary>Requests allowed per window, per caller.</summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>Length of the window in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>How many requests may wait for the next window instead of being rejected outright.</summary>
    public int QueueLimit { get; set; }
}
