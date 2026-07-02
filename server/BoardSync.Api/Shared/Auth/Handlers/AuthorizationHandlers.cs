using BoardSync.Api.Data;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BoardSync.Api.Shared.Auth.Handlers;

public class EmailConfirmedRequirement : IAuthorizationRequirement { }

public class EmailConfirmedHandler : AuthorizationHandler<EmailConfirmedRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EmailConfirmedRequirement requirement)
    {
        var emailConfirmedClaim = context.User.FindFirst("email_confirmed");

        if (emailConfirmedClaim != null &&
            bool.TryParse(emailConfirmedClaim.Value, out var isConfirmed) &&
            isConfirmed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public class ActiveUserRequirement : IAuthorizationRequirement { }

public class ActiveUserHandler : AuthorizationHandler<ActiveUserRequirement>
{
    private readonly BoardSyncDbContext _context;

    public ActiveUserHandler(BoardSyncDbContext context)
    {
        _context = context;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveUserRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);

        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
        {
            var now = DateTime.UtcNow;
            var isEligible = await _context.Users.AnyAsync(u =>
                u.Id == userId &&
                u.IsActive &&
                (!u.IsLocked || (u.LockedUntil.HasValue && u.LockedUntil.Value <= now)));

            if (isEligible)
            {
                context.Succeed(requirement);
            }
        }
    }
}

public class OwnershipRequirement : IAuthorizationRequirement
{
    public string ResourceIdParameter { get; }

    public OwnershipRequirement(string resourceIdParameter = "id")
    {
        ResourceIdParameter = resourceIdParameter;
    }
}

public class OwnershipHandler : AuthorizationHandler<OwnershipRequirement, object>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public OwnershipHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnershipRequirement requirement,
        object resource)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) return Task.CompletedTask;

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var currentUserId))
        {
            return Task.CompletedTask;
        }

        // Try to get resource ID from route parameters
        if (httpContext.Request.RouteValues.TryGetValue(requirement.ResourceIdParameter, out var resourceIdObj) &&
            Guid.TryParse(resourceIdObj?.ToString(), out var resourceUserId))
        {
            if (currentUserId == resourceUserId)
            {
                context.Succeed(requirement);
            }
        }
        // If resource is a domain object with UserId property
        else if (resource != null)
        {
            var userIdProperty = resource.GetType().GetProperty("UserId");
            if (userIdProperty != null && userIdProperty.GetValue(resource) is Guid resourceOwnerUserId)
            {
                if (currentUserId == resourceOwnerUserId)
                {
                    context.Succeed(requirement);
                }
            }
        }

        return Task.CompletedTask;
    }
}