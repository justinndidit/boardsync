using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Shared.Auth.Authorization;

/// <summary>
/// Declares the permission an action needs, and the route parameter naming what it applies to.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a hand-written guard as the first line of the action body. Two things follow from
/// moving it onto the signature. Authorization runs <em>before</em> the handler, so an endpoint no
/// longer loads a record only to discard it on denial — and no longer reveals, through the
/// difference between 403 and 404, that an id it refused names something real. And whether an
/// endpoint is guarded at all becomes a question reflection can answer, which is what
/// <c>EveryEndpointIsGuarded</c> in the test project asks of every action at build time.
/// </para>
/// <para>
/// <paramref name="permission"/> is one of the constants on <see cref="Permissions"/>, and
/// <see cref="From"/> is the name of a route parameter on the action. The parameter does not have to
/// be the scope itself: <c>workItemId</c> resolves through the item to its project, <c>sprintId</c>
/// through the sprint to its team. See <see cref="IScopeResolver"/>.
/// </para>
/// </remarks>
/// <param name="permission">The capability required, from <see cref="Permissions"/>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    /// <summary>The capability required.</summary>
    public string Permission { get; } = permission;

    /// <summary>
    /// The route parameter naming what the permission applies to — <c>projectId</c>,
    /// <c>workItemId</c>, and so on. Must have a registered <see cref="IScopeResolver"/>.
    /// </summary>
    public string From { get; init; } = string.Empty;
}

/// <summary>
/// Declares that an action requires a permission held <em>somewhere</em>, for the few endpoints that
/// have no scope to check.
/// </summary>
/// <remarks>
/// <para>
/// Looking a user up by email during an invite is the case this exists for: the person being looked
/// up is by definition not yet in your organization, so there is no scope the check could name.
/// What can be established is that the caller administers something.
/// </para>
/// <para>
/// A weaker guarantee than <see cref="RequirePermissionAttribute"/> and not a substitute for it.
/// Reach for this only when no scope exists — if the route carries one, name it.
/// </para>
/// </remarks>
/// <param name="permission">The capability required, from <see cref="Permissions"/>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePermissionAnywhereAttribute(string permission) : Attribute
{
    /// <summary>The capability required, at any scope.</summary>
    public string Permission { get; } = permission;
}

/// <summary>
/// Marks an action as deliberately unguarded, so the coverage test can tell "no permission needed"
/// apart from "somebody forgot".
/// </summary>
/// <remarks>
/// Every use needs the reason in <see cref="Because"/>. The point is not documentation for its own
/// sake — it is that an exemption should cost a sentence of justification, so adding one is a
/// decision rather than the easy way to make a failing test pass.
/// </remarks>
/// <param name="because">Why this action needs no permission check.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NoPermissionRequiredAttribute(string because) : Attribute
{
    /// <summary>Why this action needs no permission check.</summary>
    public string Because { get; } = because;
}

/// <summary>
/// Marks an action whose authorization is real but conditional, and therefore lives in the body
/// rather than on the signature.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="NoPermissionRequiredAttribute"/>. Both satisfy the coverage
/// test, but they mean opposite things — one says no check is needed, the other says a check exists
/// that an attribute cannot express — and conflating them would make the exemption list useless for
/// telling which endpoints still deserve scrutiny.
/// </remarks>
/// <param name="because">Why the check cannot be expressed as an attribute.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PermissionCheckedInActionAttribute(string because) : Attribute
{
    /// <summary>Why the check cannot be expressed as an attribute.</summary>
    public string Because { get; } = because;
}
