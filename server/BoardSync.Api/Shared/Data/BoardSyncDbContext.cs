using BoardSync.Api.Shared.Auth.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public class BoardSyncDbContext : DbContext
{
    public BoardSyncDbContext(DbContextOptions<BoardSyncDbContext> options) : base(options)
    {
    }

    // Authentication entities
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure User entity
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Email).IsUnique();
            
            entity.Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(320); // RFC 5321 standard max email length
                
            entity.Property(u => u.FirstName)
                .IsRequired()
                .HasMaxLength(50);
                
            entity.Property(u => u.LastName)
                .IsRequired()
                .HasMaxLength(50);
                
            entity.Property(u => u.DisplayName)
                .IsRequired()
                .HasMaxLength(100);
                
            entity.Property(u => u.PasswordHash)
                .IsRequired()
                .HasMaxLength(255);
                
            entity.Property(u => u.ProfilePictureUrl)
                .HasMaxLength(2048);
                
            entity.Property(u => u.EmailConfirmationToken)
                .HasMaxLength(255);
                
            entity.Property(u => u.PasswordResetToken)
                .HasMaxLength(255);
                
            entity.Property(u => u.RefreshToken)
                .HasMaxLength(255);

            // Indexes for performance
            entity.HasIndex(u => u.EmailConfirmationToken);
            entity.HasIndex(u => u.PasswordResetToken);
            entity.HasIndex(u => u.IsActive);
            entity.HasIndex(u => u.CreatedAt);

            // Configure relationships
            entity.HasMany(u => u.RefreshTokens)
                .WithOne(rt => rt.User)
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure RefreshToken entity
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(rt => rt.Id);
            
            entity.Property(rt => rt.Token)
                .IsRequired()
                .HasMaxLength(255);
                
            entity.Property(rt => rt.CreatedByIp)
                .IsRequired()
                .HasMaxLength(45); // IPv6 max length
                
            entity.Property(rt => rt.RevokedByIp)
                .HasMaxLength(45);
                
            entity.Property(rt => rt.ReplacedByToken)
                .HasMaxLength(255);
                
            entity.Property(rt => rt.ReasonRevoked)
                .HasMaxLength(255);

            // Indexes for performance
            entity.HasIndex(rt => rt.Token).IsUnique();
            entity.HasIndex(rt => rt.UserId);
            entity.HasIndex(rt => rt.Expires);
            entity.HasIndex(rt => rt.Revoked);
            entity.HasIndex(rt => rt.Created);
        });
    }
}
