using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BoardSync.Api.Shared.Auth.Authorization;

/// <summary>
/// Enforces <see cref="RequirePermissionAttribute"/> before the action runs.
/// </summary>
/// <remarks>
/// <para>
/// Registered globally, so it sees every action; actions without the attribute are left alone and
/// the coverage test is what ensures there are none by accident.
/// </para>
/// <para>
/// <b>On telling 403 from 404.</b> The temptation is to answer 404 for every refusal, so that no
/// response ever confirms an id names something real. That over-corrects: someone who can see a
/// project but lacks <c>project:admin</c> knows perfectly well it exists, and answering "not found"
/// to their rename attempt is a lie that makes the UI unexplainable. The rule here splits on
/// visibility instead:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Cannot even read the scope → <b>404</b>. Indistinguishable from an id that does not exist,
///     which is the disclosure this closes.
///   </description></item>
///   <item><description>
///     Can read the scope but lacks the permission → <b>403</b>, which is the truth and is
///     actionable.
///   </description></item>
/// </list>
/// </remarks>
public sealed class PermissionAuthorizationFilter(
    IRbacService rbac,
    ICurrentUserContext currentUser,
    ScopeResolverRegistry resolvers,
    ILogger<PermissionAuthorizationFilter> logger) : IAsyncAuthorizationFilter
{
    /// <summary>
    /// The permission that means "may see this at all", per scope. Lacking it is what turns a
    /// refusal into a 404.
    /// </summary>
    private static string ReadPermissionFor(RoleScope scope) => scope switch
    {
        RoleScope.Organization => Permissions.OrgRead,
        RoleScope.Team => Permissions.TeamRead,
        RoleScope.Project => Permissions.ProjectRead,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled RoleScope value.")
    };

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var ct = context.HttpContext.RequestAborted;

        var anywhere = context.ActionDescriptor.EndpointMetadata
            .OfType<RequirePermissionAnywhereAttribute>()
            .FirstOrDefault();

        if (anywhere is not null)
        {
            // No scope to resolve, so no visibility question either: 403 is the only honest answer.
            if (!await rbac.HasPermissionAnywhereAsync(currentUser.UserId, anywhere.Permission, ct))
            {
                logger.LogInformation(
                    "User {UserId} denied {Permission} (not held at any scope)",
                    currentUser.UserId, anywhere.Permission);

                context.Result = Denied(context, StatusCodes.Status403Forbidden);
            }

            return;
        }

        var required = context.ActionDescriptor.EndpointMetadata
            .OfType<RequirePermissionAttribute>()
            .FirstOrDefault();

        if (required is null) return;

        if (!TryReadRouteGuid(context, required.From, out var value))
        {
            // The route did not carry the parameter the attribute names. That is a wiring mistake
            // rather than a caller mistake, and failing closed is the only safe reading of it.
            logger.LogError(
                "Action {Action} requires {Permission} from route parameter '{Parameter}', which is " +
                "missing or not a GUID. Denying.",
                context.ActionDescriptor.DisplayName, required.Permission, required.From);

            context.Result = Denied(context, StatusCodes.Status403Forbidden);
            return;
        }

        var resolver = resolvers.For(required.From);

        if (resolver is null)
        {
            logger.LogError(
                "No scope resolver is registered for route parameter '{Parameter}' used by {Action}. Denying.",
                required.From, context.ActionDescriptor.DisplayName);

            context.Result = Denied(context, StatusCodes.Status403Forbidden);
            return;
        }

        var scope = await resolver.ResolveAsync(value, ct);

        // Nothing of that id. Same answer as "you may not see it", by design.
        if (scope is not ResolvedScope target)
        {
            context.Result = Denied(context, StatusCodes.Status404NotFound);
            return;
        }

        var userId = currentUser.UserId;

        if (await rbac.HasPermissionAsync(userId, required.Permission, target.Scope, target.ScopeId, ct))
            return;

        var canSee = await rbac.HasPermissionAsync(
            userId, ReadPermissionFor(target.Scope), target.Scope, target.ScopeId, ct);

        logger.LogInformation(
            "User {UserId} denied {Permission} on {Scope}:{ScopeId} ({Outcome})",
            userId, required.Permission, target.Scope, target.ScopeId,
            canSee ? "visible, insufficient permission" : "not visible");

        context.Result = Denied(context,
            canSee ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound);
    }

    private static bool TryReadRouteGuid(
        AuthorizationFilterContext context, string parameter, out Guid value)
    {
        value = Guid.Empty;

        return !string.IsNullOrEmpty(parameter)
            && context.RouteData.Values.TryGetValue(parameter, out var raw)
            && Guid.TryParse(raw?.ToString(), out value);
    }

    /// <summary>
    /// Bodies matching what the exception middleware produces for the same statuses, so a refusal
    /// looks the same to a client whether it came from here or from a service throwing.
    /// </summary>
    private static ObjectResult Denied(AuthorizationFilterContext context, int statusCode)
    {
        var message = statusCode == StatusCodes.Status404NotFound
            ? "Resource not found"
            : "Access forbidden";

        return new ObjectResult(new ApiResponse(false, message)) { StatusCode = statusCode };
    }
}
