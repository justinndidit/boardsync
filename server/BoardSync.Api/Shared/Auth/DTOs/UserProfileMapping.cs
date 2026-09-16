using BoardSync.Api.Shared.Auth.Models;

namespace BoardSync.Api.Shared.Auth.DTOs;

/// <summary>
/// The one definition of how a <see cref="User"/> becomes a <see cref="UserProfile"/>.
/// </summary>
/// <remarks>
/// There were three copies of this — login, profile read, and profile update — and they had already
/// drifted on the one field that varies: <see cref="User.ProfilePictureUrl"/> is a non-nullable
/// column that holds <c>""</c> for somebody who has never set a picture, while the DTO declares it
/// nullable and every client tests it for null. Sending <c>""</c> happened to work because it is
/// falsy in JavaScript, which is the kind of accident that stops being true the moment a client is
/// written in something else.
/// </remarks>
public static class UserProfileMapping
{
    /// <summary>Empty means no picture, and no picture is <c>null</c> on the wire.</summary>
    public static string? PictureOrNull(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url;

    /// <summary>
    /// Projects a loaded user. Roles are left null — they are scope-specific, and the caller that
    /// wants them asks RBAC, which is the thing that actually knows.
    /// </summary>
    public static UserProfile ToProfile(this User user) =>
        new(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.DisplayName,
            PictureOrNull(user.ProfilePictureUrl),
            user.IsEmailConfirmed,
            user.IsActive,
            user.CreatedAt);
}
