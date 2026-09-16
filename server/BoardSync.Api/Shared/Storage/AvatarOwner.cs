namespace BoardSync.Api.Shared.Storage;

/// <summary>What kind of thing an avatar belongs to.</summary>
public enum AvatarOwnerKind
{
    /// <summary>A person's profile picture.</summary>
    User,

    /// <summary>An organization's logo.</summary>
    Organization,
}

/// <summary>
/// Who an avatar belongs to, and therefore where it may be written.
/// </summary>
/// <remarks>
/// <para>
/// A user's profile picture and an organization's logo are the same kind of object — a small
/// public image, same formats, same size ceiling, same public-read container — differing only in
/// who is allowed to replace one. So they share the storage and separate here, at the prefix,
/// rather than through a second copy of the Azure client.
/// </para>
/// <para>
/// This is the boundary the whole direct-upload design rests on. The prefix is built from the
/// authorized subject — the token's user id, or the organization the caller holds
/// <c>org:admin</c> on — and never from anything in the request body, so a signed URL cannot be
/// aimed at an avatar the caller does not own.
/// </para>
/// </remarks>
/// <param name="Kind">Which sort of owner.</param>
/// <param name="Id">The owner's id.</param>
public readonly record struct AvatarOwner(AvatarOwnerKind Kind, Guid Id)
{
    public static AvatarOwner ForUser(Guid userId) => new(AvatarOwnerKind.User, userId);

    public static AvatarOwner ForOrganization(Guid organizationId) =>
        new(AvatarOwnerKind.Organization, organizationId);

    /// <summary>
    /// The container path every one of this owner's avatars sits under.
    /// </summary>
    /// <remarks>
    /// Kind-first, so the two namespaces cannot collide even in the impossible case of a user id
    /// equalling an organization id — and so listing the container is legible to a person.
    ///
    /// User avatars written before organizations were supported live at <c>{userId}/…</c> with no
    /// kind segment. Nothing reads them back by prefix: the stored URL is absolute, and deleting a
    /// replaced one goes through the container-relative path, so the older layout keeps working
    /// and is simply never written again.
    /// </remarks>
    public string Prefix => Kind switch
    {
        AvatarOwnerKind.User => $"users/{Id:D}",
        AvatarOwnerKind.Organization => $"organizations/{Id:D}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown avatar owner."),
    };
}
