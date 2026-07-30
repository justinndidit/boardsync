using System.Text.RegularExpressions;

namespace BoardSync.Api.Modules.OrgProject.Domain.Helpers;

/// <summary>
/// URL-friendly identifier generation. Shared by organizations and projects so both produce
/// slugs that satisfy the same <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> shape the request DTOs validate.
/// </summary>
public static class Slug
{
    /// <summary>Lower-cases the input and collapses every run of non-alphanumerics into a single hyphen.</summary>
    public static string From(string input) =>
        Regex.Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
