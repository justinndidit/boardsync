namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// A scope named as one string — <c>org:{guid}</c>, <c>team:{guid}</c>, <c>project:{guid}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists so a client can ask about several scopes in one request without the shape of the question
/// depending on which kind each one is. Everywhere else in the API a scope arrives as a route
/// parameter whose name says what it is; the capabilities endpoint is the one place that takes a
/// heterogeneous list.
/// </para>
/// <para>
/// The wire format matches <c>Shared.Kernel.Events.Topic</c> deliberately — a client subscribing to
/// <c>project:{id}</c> over the hub and asking what it may do on <c>project:{id}</c> should not have
/// to hold two spellings of the same idea. This parses independently rather than reusing that type,
/// because topics also name users and sprints, which are not role scopes, and a shared parser would
/// have to answer "what <see cref="RoleScope"/> is <c>user:…</c>" with a lie.
/// </para>
/// </remarks>
public readonly record struct ScopeRef(RoleScope Scope, Guid Id)
{
    /// <summary>Renders back to the wire form.</summary>
    public override string ToString() => $"{Prefix(Scope)}:{Id}";

    private static string Prefix(RoleScope scope) => scope switch
    {
        RoleScope.Organization => "org",
        RoleScope.Team => "team",
        RoleScope.Project => "project",
        _ => "unknown"
    };

    /// <summary>
    /// Parses a scope reference, or returns false when it is not one.
    /// </summary>
    /// <remarks>
    /// Callers get this straight from a client, so a malformed value is a bad request rather than an
    /// exception.
    /// </remarks>
    public static bool TryParse(string? value, out ScopeRef result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;

        if (!Guid.TryParse(value.AsSpan(separator + 1), out var id)) return false;

        RoleScope scope;

        switch (value.AsSpan(0, separator))
        {
            case "org": scope = RoleScope.Organization; break;
            case "team": scope = RoleScope.Team; break;
            case "project": scope = RoleScope.Project; break;
            default: return false;
        }

        result = new ScopeRef(scope, id);
        return true;
    }
}
