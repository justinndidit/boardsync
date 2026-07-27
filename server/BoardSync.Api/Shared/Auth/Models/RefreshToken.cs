using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace BoardSync.Api.Shared.Auth.Models;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Token { get; set; } = string.Empty;
    public DateTime Expires { get; set; }

    /// <summary>In-memory only. For queries use <see cref="RefreshTokenQueryExtensions.WhereActive"/>.</summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= Expires;

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedByIp { get; set; } = string.Empty;
    public DateTime? Revoked { get; set; }
    public string? RevokedByIp { get; set; }
    public string? ReplacedByToken { get; set; }
    public string? ReasonRevoked { get; set; }

    /// <summary>In-memory only. For queries use <see cref="RefreshTokenQueryExtensions.WhereActive"/>.</summary>
    [NotMapped]
    public bool IsActive => Revoked == null && !IsExpired;

    // Foreign key
    public Guid UserId { get; set; }
    public virtual User User { get; set; } = null!;
}

public static class RefreshTokenQueryExtensions
{
    /// <summary>
    /// Server-side equivalent of <see cref="RefreshToken.IsActive"/>. The property itself is
    /// unmapped and cannot be translated to SQL, so it must not be used inside a query predicate.
    /// </summary>
    public static IQueryable<RefreshToken> WhereActive(this IQueryable<RefreshToken> source) =>
        source.Where(rt => rt.Revoked == null && rt.Expires > DateTime.UtcNow);
}