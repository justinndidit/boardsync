using BoardSync.Api.Shared.Auth.Repositories;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.AspNetCore.Authorization;
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
    private readonly IUserRepository _users;

    public ActiveUserHandler(IUserRepository users)
    {
        _users = users;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveUserRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);

        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
        {
            var isEligible = await _users.IsEligibleForAccessAsync(userId);

            if (isEligible)
            {
                context.Succeed(requirement);
            }
        }
    }
}
