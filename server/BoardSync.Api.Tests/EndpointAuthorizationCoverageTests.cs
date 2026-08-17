using System.Reflection;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace BoardSync.Api.Tests;

/// <summary>
/// Asks of every endpoint the question no hand-written test thinks to ask: is it guarded at all?
/// </summary>
/// <remarks>
/// <para>
/// Authorization used to be a convention — the first line of an action body — which meant a new
/// endpoint that omitted it was silently reachable by any authenticated user, and nothing anywhere
/// would notice. That is the failure these tests exist to make impossible: they enumerate the real
/// controller surface by reflection, so they cover endpoints nobody remembered to write a case for,
/// including ones added after this file was last read.
/// </para>
/// <para>
/// An endpoint satisfies them by carrying <see cref="RequirePermissionAttribute"/>, or by carrying
/// one of the two exemptions with a stated reason. The exemptions are deliberately separate:
/// <see cref="NoPermissionRequiredAttribute"/> means no check is needed,
/// <see cref="PermissionCheckedInActionAttribute"/> means a real check exists that an attribute
/// cannot express. Reading the exemption list should tell you which endpoints still deserve
/// scrutiny, which it cannot do if both look the same.
/// </para>
/// </remarks>
public class EndpointAuthorizationCoverageTests
{
    private static readonly Assembly Api = typeof(Permissions).Assembly;

    /// <summary>Every action method on every controller in the API.</summary>
    public static TheoryData<string> AllActions()
    {
        var data = new TheoryData<string>();

        foreach (var action in Actions())
            data.Add($"{action.DeclaringType!.Name}.{action.Name}");

        return data;
    }

    private static IEnumerable<MethodInfo> Actions() =>
        Api.GetTypes()
           .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
           .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
           .Where(m => !m.IsSpecialName)
           .Where(m => m.GetCustomAttributes().Any(a => a is IActionHttpMethodProvider));

    private static MethodInfo Find(string name) =>
        Actions().Single(m => $"{m.DeclaringType!.Name}.{m.Name}" == name);

    [Theory]
    [MemberData(nameof(AllActions))]
    public void EveryEndpointDeclaresItsAuthorization(string action)
    {
        var method = Find(action);

        var declared =
            method.GetCustomAttribute<RequirePermissionAttribute>() is not null ||
            method.GetCustomAttribute<RequirePermissionAnywhereAttribute>() is not null ||
            method.GetCustomAttribute<NoPermissionRequiredAttribute>() is not null ||
            method.GetCustomAttribute<PermissionCheckedInActionAttribute>() is not null ||
            method.GetCustomAttribute<AllowAnonymousAttribute>() is not null ||
            method.DeclaringType!.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

        Assert.True(declared,
            $"{action} declares no authorization. Add [RequirePermission(...)], or one of " +
            "[NoPermissionRequired(\"why\")] / [PermissionCheckedInAction(\"why\")] with a reason.");
    }

    /// <summary>
    /// A permission that is not one of the declared constants is a typo, and a typo is a permission
    /// nobody holds — which fails closed, silently, and looks like a broken feature rather than a
    /// broken guard.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllActions))]
    public void RequiredPermissionsAreRealOnes(string action)
    {
        var method = Find(action);

        var permission =
            method.GetCustomAttribute<RequirePermissionAttribute>()?.Permission
            ?? method.GetCustomAttribute<RequirePermissionAnywhereAttribute>()?.Permission;

        if (permission is null) return;

        var known = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Contains(permission, known);
    }

    /// <summary>
    /// The route parameter an attribute resolves from must actually appear in that action's route,
    /// or the filter cannot read it and denies every request — a guard that fails closed so hard the
    /// endpoint is simply broken.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllActions))]
    public void ScopeParametersExistOnTheirRoute(string action)
    {
        var method = Find(action);
        var required = method.GetCustomAttribute<RequirePermissionAttribute>();

        if (required is null) return;

        Assert.False(string.IsNullOrWhiteSpace(required.From),
            $"{action} requires {required.Permission} but names no route parameter to resolve it from.");

        var templates = method.GetCustomAttributes()
            .OfType<IRouteTemplateProvider>()
            .Select(r => r.Template ?? string.Empty)
            .ToList();

        Assert.True(
            templates.Any(t => t.Contains($"{{{required.From}", StringComparison.OrdinalIgnoreCase)),
            $"{action} resolves scope from '{required.From}', which is not in its route " +
            $"({string.Join(", ", templates)}).");
    }

    /// <summary>
    /// Every exemption states why. The point is not the prose — it is that an exemption should cost
    /// a sentence of justification, so it is a decision rather than the quickest way past a failing
    /// test.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllActions))]
    public void ExemptionsAreJustified(string action)
    {
        var method = Find(action);

        var reason =
            method.GetCustomAttribute<NoPermissionRequiredAttribute>()?.Because
            ?? method.GetCustomAttribute<PermissionCheckedInActionAttribute>()?.Because;

        if (reason is null) return;

        Assert.True(reason.Trim().Length >= 20,
            $"{action} is exempt with no real reason given: '{reason}'.");
    }
}
