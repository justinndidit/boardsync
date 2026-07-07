using BoardSync.Api.Modules.OrgProject.Models;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public class BoardSyncDbContext : DbContext
{
    public BoardSyncDbContext(DbContextOptions<BoardSyncDbContext> options) : base(options)
    {
    }

    // ---- IAM module ----
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

    // ---- RBAC module ----
    public DbSet<RoleAssignment> RoleAssignments { get; set; } = null!;

    // ---- OrgProject module ----
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<OrganizationMembership> OrganizationMemberships { get; set; } = null!;
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<Team> Teams { get; set; } = null!;
    public DbSet<TeamMembership> TeamMemberships { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ----------------------------------------------------------------
        // RBAC Module — schema: iam
        // ----------------------------------------------------------------
        modelBuilder.Entity<RoleAssignment>(entity =>
        {
            entity.ToTable("RoleAssignments", "iam");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.UserId, r.Role, r.Scope, r.ScopeId }).IsUnique();
            entity.HasIndex(r => new { r.Scope, r.ScopeId });
            entity.HasIndex(r => r.UserId);

            entity.Property(r => r.Role).HasConversion<string>().HasMaxLength(30);
            entity.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20);
        });

        // ----------------------------------------------------------------
        // OrgProject Module — schema: org
        // ----------------------------------------------------------------
        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("Organizations", "org");
            entity.HasKey(o => o.Id);
            entity.HasIndex(o => o.Slug).IsUnique();
            entity.HasIndex(o => o.IsActive);

            entity.Property(o => o.Slug).IsRequired().HasMaxLength(60);
            entity.Property(o => o.Name).IsRequired().HasMaxLength(100);
            entity.Property(o => o.Description).HasMaxLength(500);
            entity.Property(o => o.AvatarUrl).HasMaxLength(2048);

            entity.HasMany(o => o.Projects)
                .WithOne(p => p.Organization)
                .HasForeignKey(p => p.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(o => o.Members)
                .WithOne(m => m.Organization)
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationMembership>(entity =>
        {
            entity.ToTable("OrganizationMemberships", "org");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique();
            entity.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects", "org");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.OrganizationId, p.Slug }).IsUnique();
            entity.HasIndex(p => p.IsActive);

            entity.Property(p => p.Slug).IsRequired().HasMaxLength(60);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(500);

            entity.HasMany(p => p.Teams)
                .WithOne(t => t.Project)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("Teams", "org");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.ProjectId, t.Name }).IsUnique();
            entity.HasIndex(t => t.IsActive);

            entity.Property(t => t.Name).IsRequired().HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(500);

            entity.HasMany(t => t.Members)
                .WithOne(m => m.Team)
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMembership>(entity =>
        {
            entity.ToTable("TeamMemberships", "org");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.TeamId, m.UserId }).IsUnique();
            entity.HasIndex(m => m.UserId);
        });

        // ----------------------------------------------------------------
        // IAM Module — User entity
        // ----------------------------------------------------------------
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
