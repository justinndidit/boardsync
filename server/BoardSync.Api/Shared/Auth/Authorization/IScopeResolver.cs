using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Shared.Auth.Authorization;

/// <summary>
/// Where a scope was found, or why it could not be.
/// </summary>
/// <param name="Scope">The scope kind the permission should be checked against.</param>
/// <param name="ScopeId">The scope's id.</param>
public readonly record struct ResolvedScope(RoleScope Scope, Guid ScopeId);

/// <summary>
/// Turns a route parameter into the scope a permission should be checked against.
/// </summary>
/// <remarks>
/// <para>
/// Most endpoints are keyed on something that is not itself a scope. <c>workItemId</c> names a work
/// item whose project carries the permission; <c>sprintId</c> names a sprint whose team does;
/// <c>commentId</c>, <c>columnId</c> and <c>linkId</c> are each one hop further out. A resolver is
/// that hop, and it is the reason authorization can happen before the handler rather than after the
/// handler has already fetched the record.
/// </para>
/// <para>
/// Each resolver is registered by the module that owns the data it walks, so this does not become a
/// place where every module's queries accumulate.
/// </para>
/// </remarks>
public interface IScopeResolver
{
    /// <summary>
    /// The route parameter this resolver handles — <c>workItemId</c>, <c>projectId</c>, and so on.
    /// Matched case-insensitively against the route.
    /// </summary>
    string RouteParameter { get; }

    /// <summary>
    /// Resolves the parameter's value to a scope, or null when nothing of that id exists.
    /// </summary>
    /// <remarks>
    /// Null means "no such record", which the filter reports as 404 — the same answer an
    /// unauthorized caller gets, so the two are indistinguishable from outside.
    /// </remarks>
    Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct);
}

/// <summary>
/// A resolver for a parameter that already names a scope — <c>orgId</c>, <c>teamId</c>,
/// <c>projectId</c>.
/// </summary>
/// <remarks>
/// Deliberately does not check that the scope exists. A caller with no access must not be able to
/// tell an id that exists from one that does not, and the permission check answers both with 404
/// anyway; verifying existence first would only add a query whose result changes nothing.
/// </remarks>
public sealed class DirectScopeResolver(string routeParameter, RoleScope scope) : IScopeResolver
{
    public string RouteParameter { get; } = routeParameter;

    public Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct) =>
        Task.FromResult<ResolvedScope?>(new ResolvedScope(scope, value));
}

/// <summary>
/// The resolvers available, indexed by route parameter name.
/// </summary>
public sealed class ScopeResolverRegistry
{
    private readonly Dictionary<string, IScopeResolver> _byParameter;

    public ScopeResolverRegistry(IEnumerable<IScopeResolver> resolvers)
    {
        _byParameter = resolvers.ToDictionary(
            r => r.RouteParameter, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The resolver for a route parameter, or null if none is registered.</summary>
    public IScopeResolver? For(string routeParameter) =>
        _byParameter.GetValueOrDefault(routeParameter);

    /// <summary>Every parameter name that can be resolved — used by the coverage test's diagnostics.</summary>
    public IReadOnlyCollection<string> KnownParameters => _byParameter.Keys;
}
