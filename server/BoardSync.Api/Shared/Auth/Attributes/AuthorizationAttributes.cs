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

// RequireOwnershipAttribute and its handler were removed: the requirement needed a resource
// instance passed to IAuthorizationService.AuthorizeAsync, which no call site ever did, so the
// policy could only ever fail. Resource ownership is currently enforced inside the services
// (e.g. comment author checks in WorkItemService).
//
// RateLimitAttribute was also removed — it carried limits that nothing read. Rate limiting is
// configured in Program.cs and applied with [EnableRateLimiting] plus the global limiter.