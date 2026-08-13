using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Models;

namespace BoardSync.Api.Shared.Auth.Repositories;

/// <summary>
/// Data access for user accounts and their refresh tokens — the <c>public.Users</c> and
/// <c>public.RefreshTokens</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// Every email lookup normalizes its argument the same way, so callers pass whatever the user
/// typed and do not each have to remember to lower-case it. Getting that wrong in one place is how
/// duplicate accounts appear.
/// </para>
/// <para>
/// Mutations are staged and persisted by <see cref="SaveChangesAsync"/>, so a service can change a
/// user and revoke their tokens in a single write.
/// </para>
/// </remarks>
public interface IUserRepository
{
    // ── Users ─────────────────────────────────────────────────────────────────

    /// <summary>User by ID, tracked for mutation, or null.</summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>User by email, tracked for mutation, or null. Email is normalized for you.</summary>
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Whether an account already exists for this email.</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Whether the user exists, is active and has a confirmed email.
    /// </summary>
    Task<bool> IsActiveAndConfirmedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Whether the user may currently be served at all: active, and either unlocked or holding a
    /// lock whose expiry has passed.
    /// </summary>
    /// <remarks>
    /// The expiry is compared in the database rather than through <c>User.IsLocked</c>, which is an
    /// unmapped property and would silently pull every user into memory to evaluate.
    /// </remarks>
    Task<bool> IsEligibleForAccessAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Active user by ID as a public profile, or null. Projected in the database so nothing
    /// security-bearing — password hash, reset and confirmation tokens, lockout counters — is
    /// ever loaded for a read that only needs the public view.
    /// </summary>
    Task<UserProfile?> GetProfileByIdAsync(Guid userId, CancellationToken ct = default);

    /// <inheritdoc cref="GetProfileByIdAsync" />
    Task<UserProfile?> GetProfileByEmailAsync(string email, CancellationToken ct = default);

    void Add(User user);
    void Remove(User user);

    // ── Refresh tokens ────────────────────────────────────────────────────────

    /// <summary>Refresh token by its value, with the owning user loaded, or null.</summary>
    Task<RefreshToken?> GetRefreshTokenWithUserAsync(string token, CancellationToken ct = default);

    /// <summary>Every unrevoked, unexpired token for a user, tracked for revocation.</summary>
    Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// One user's refresh token by value, tracked, or null. Scoped to the user on purpose: a token
    /// is only revocable by the account that owns it, and matching on the value alone would let a
    /// caller revoke someone else's session by guessing.
    /// </summary>
    Task<RefreshToken?> GetRefreshTokenForUserAsync(string token, Guid userId, CancellationToken ct = default);

    void AddRefreshToken(RefreshToken token);

    // ── Unit of work ──────────────────────────────────────────────────────────

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
