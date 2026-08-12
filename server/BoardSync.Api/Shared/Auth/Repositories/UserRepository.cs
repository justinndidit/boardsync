using BoardSync.Api.Data;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Shared.Auth.Repositories;

/// <inheritdoc />
public class UserRepository : IUserRepository
{
    private readonly BoardSyncDbContext _context;

    public UserRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        return _context.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        return _context.Users.AnyAsync(u => u.Email == normalized, ct);
    }

    public Task<bool> IsActiveAndConfirmedAsync(Guid userId, CancellationToken ct = default) =>
        _context.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.IsEmailConfirmed, ct);

    public Task<bool> IsEligibleForAccessAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return _context.Users.AnyAsync(u =>
            u.Id == userId
            && u.IsActive
            && (!u.IsLocked || (u.LockedUntil.HasValue && u.LockedUntil.Value <= now)), ct);
    }

    public Task<UserProfile?> GetProfileByIdAsync(Guid userId, CancellationToken ct = default) =>
        ToProfile(_context.Users.Where(u => u.IsActive && u.Id == userId)).FirstOrDefaultAsync(ct);

    public Task<UserProfile?> GetProfileByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        return ToProfile(_context.Users.Where(u => u.IsActive && u.Email == normalized))
            .FirstOrDefaultAsync(ct);
    }

    public void Add(User user) => _context.Users.Add(user);

    public void Remove(User user) => _context.Users.Remove(user);

    // ── Refresh tokens ────────────────────────────────────────────────────────

    public Task<RefreshToken?> GetRefreshTokenWithUserAsync(string token, CancellationToken ct = default) =>
        _context.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == token, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await _context.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .WhereActive()
            .ToListAsync(ct);

    public Task<RefreshToken?> GetRefreshTokenForUserAsync(
        string token,
        Guid userId,
        CancellationToken ct = default) =>
        _context.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == token && rt.UserId == userId, ct);

    public void AddRefreshToken(RefreshToken token) => _context.RefreshTokens.Add(token);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    // ── Shared query shapes ───────────────────────────────────────────────────

    /// <summary>
    /// Projects users to the public profile. Roles are left null — they are scope-specific and the
    /// caller that wants them asks RBAC, which is the thing that actually knows.
    /// </summary>
    /// <remarks>
    /// Takes the filtered query rather than applying the projection first. Filtering after the
    /// Select would build a predicate over <see cref="UserProfile"/> instead of the entity, which
    /// EF cannot translate — it fails the request at runtime, not at build time.
    /// </remarks>
    private static IQueryable<UserProfile> ToProfile(IQueryable<User> users) =>
        users.Select(u => new UserProfile(
            u.Id, u.Email, u.FirstName, u.LastName,
            u.DisplayName, u.ProfilePictureUrl,
            u.IsEmailConfirmed, u.IsActive, u.CreatedAt));

    /// <summary>
    /// One definition of what an email key looks like. Every lookup goes through here so a caller
    /// that forgets to normalize cannot create a second account for the same address.
    /// </summary>
    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
