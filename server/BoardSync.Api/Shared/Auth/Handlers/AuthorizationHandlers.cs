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
