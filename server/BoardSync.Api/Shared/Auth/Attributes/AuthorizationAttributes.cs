using Microsoft.AspNetCore.Authorization;

namespace BoardSync.Api.Shared.Auth.Attributes;

/// <summary>
/// Requires the user to be authenticated
/// </summary>
public class RequireAuthenticationAttribute : AuthorizeAttribute
{
    public RequireAuthenticationAttribute() : base("RequireAuthentication")
    {
    }
}

/// <summary>
/// Requires the user to have a confirmed email address
/// </summary>
public class RequireEmailConfirmedAttribute : AuthorizeAttribute
{
    public RequireEmailConfirmedAttribute() : base("RequireEmailConfirmed")
    {
    }
}

/// <summary>
/// Requires the user to be active (not locked or deactivated)
/// </summary>
public class RequireActiveUserAttribute : AuthorizeAttribute
{
    public RequireActiveUserAttribute() : base("RequireActiveUser")
    {
    }
}

/// <summary>
/// Allows only users who own the resource or are administrators
/// </summary>
public class RequireOwnershipAttribute : AuthorizeAttribute
{
    public RequireOwnershipAttribute() : base("RequireOwnership")
    {
    }
}

/// <summary>
/// Rate limiting attribute for sensitive operations
/// </summary>
public class RateLimitAttribute : Attribute
{
    public int RequestsPerMinute { get; }
    public int RequestsPerHour { get; }

    public RateLimitAttribute(int requestsPerMinute = 10, int requestsPerHour = 100)
    {
        RequestsPerMinute = requestsPerMinute;
        RequestsPerHour = requestsPerHour;
    }
}