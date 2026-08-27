using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Auth.Models;
using BoardSync.Api.Modules.GitSync.Ingest;
using BoardSync.Api.Modules.Notifications.Models;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Jobs;
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

    // ---- WorkItems module ----
    public DbSet<WorkItem> WorkItems { get; set; } = null!;
    public DbSet<WorkItemComment> WorkItemComments { get; set; } = null!;
    public DbSet<WorkItemHistory> WorkItemHistory { get; set; } = null!;
    public DbSet<WorkItemLink> WorkItemLinks { get; set; } = null!;
    public DbSet<WorkItemTag> WorkItemTags { get; set; } = null!;

    // ---- RBAC module ----
    public DbSet<RoleAssignment> RoleAssignments { get; set; } = null!;

    // ---- OrgProject module ----
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<OrganizationMembership> OrganizationMemberships { get; set; } = null!;
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<Team> Teams { get; set; } = null!;
    public DbSet<TeamMembership> TeamMemberships { get; set; } = null!;

    // ---- Sprints / Boards module ----
    public DbSet<Sprint> Sprints { get; set; } = null!;
    public DbSet<SprintWorkItem> SprintWorkItems { get; set; } = null!;
    public DbSet<Board> Boards { get; set; } = null!;
    public DbSet<BoardColumn> BoardColumns { get; set; } = null!;

    // ---- Backlog module ----
    public DbSet<BacklogItem> BacklogItems { get; set; } = null!;

    // ---- Activity module ----
    public DbSet<ActivityLog> ActivityLogs { get; set; } = null!;

    // ---- Notifications module ----
    public DbSet<Notification> Notifications { get; set; } = null!;
    public DbSet<WorkItemWatcher> WorkItemWatchers { get; set; } = null!;

    // ---- GitSync module ----
    public DbSet<GitProviderInstallation> GitProviderInstallations { get; set; } = null!;
    public DbSet<RepositoryLink> RepositoryLinks { get; set; } = null!;
    public DbSet<WebhookDelivery> WebhookDeliveries { get; set; } = null!;

    // ── Shared kernel ─────────────────────────────────────────────────────────
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<Job> Jobs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ----------------------------------------------------------------
        // WorkItems Module — schema: work
        // ----------------------------------------------------------------
        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.ToTable("WorkItems", "work");
            entity.HasKey(w => w.Id);
            entity.HasIndex(w => w.ProjectId);
            entity.HasIndex(w => w.TeamId);
            entity.HasIndex(w => w.AssigneeId);
            entity.HasIndex(w => w.State);
            entity.HasIndex(w => w.Type);
            entity.HasIndex(w => w.ParentId);
            entity.HasIndex(w => w.IsActive);

            // The shape every project-scoped read uses: one project, live rows only, often narrowed
            // to a state. Postgres can bitmap-AND the three single-column indexes above instead, but
            // that costs a heap probe per candidate row — this one answers the workspace summary's
            // active-work-item count straight from the index.
            entity.HasIndex(w => new { w.ProjectId, w.IsActive, w.State });

            // What BS-142 resolves to. Unique because a reference that matched two items would make
            // the binding ambiguous, and the allocator guarantees it anyway — the index is what
            // turns "guaranteed" into "enforced".
            entity.HasIndex(w => new { w.ProjectId, w.Number }).IsUnique();

            // Postgres maintains xmin itself; mapping it costs no column and no migration, and
            // gives every work item a version EF can check on update.
            entity.Property(w => w.Version).IsRowVersion().HasColumnName("xmin").HasColumnType("xid");

            entity.Property(w => w.Title).IsRequired().HasMaxLength(255);
            entity.Property(w => w.Description).HasMaxLength(10000);
            entity.Property(w => w.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(w => w.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(w => w.Priority).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(w => w.Parent)
                .WithMany(w => w.Children)
                .HasForeignKey(w => w.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(w => w.Comments)
                .WithOne(c => c.WorkItem)
                .HasForeignKey(c => c.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(w => w.History)
                .WithOne(h => h.WorkItem)
                .HasForeignKey(h => h.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(w => w.Tags)
                .WithOne(t => t.WorkItem)
                .HasForeignKey(t => t.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(w => w.LinksFrom)
                .WithOne(l => l.Source)
                .HasForeignKey(l => l.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(w => w.LinksTo)
                .WithOne(l => l.Target)
                .HasForeignKey(l => l.TargetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkItemComment>(entity =>
        {
            entity.ToTable("WorkItemComments", "work");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.WorkItemId);
            entity.HasIndex(c => c.AuthorId);

            entity.Property(c => c.Body).IsRequired().HasMaxLength(10000);
        });

        modelBuilder.Entity<WorkItemHistory>(entity =>
        {
            entity.ToTable("WorkItemHistory", "work");
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => h.WorkItemId);
            entity.HasIndex(h => h.ChangedBy);

            // Serves the workspace notification feed, which filters by a set of projects and sorts
            // by recency. Descending on CreatedAt so the feed's ORDER BY reads straight out of the
            // index instead of sorting the matched rows.
            entity.Property(h => h.ActorType).HasConversion<string>().HasMaxLength(20);

            // The git transition rules ask "has a person changed this item's state since?", which
            // this serves directly — it is on the hot path of every webhook delivery.
            entity.HasIndex(h => new { h.WorkItemId, h.FieldName, h.CreatedAt });

            entity.HasIndex(h => new { h.ProjectId, h.CreatedAt })
                .IsDescending(false, true);

            entity.Property(h => h.FieldName).IsRequired().HasMaxLength(100);
            entity.Property(h => h.OldValue).HasMaxLength(1000);
            entity.Property(h => h.NewValue).HasMaxLength(1000);
        });

        modelBuilder.Entity<WorkItemLink>(entity =>
        {
            entity.ToTable("WorkItemLinks", "work");
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.SourceId, l.TargetId, l.LinkType }).IsUnique();
            entity.HasIndex(l => l.TargetId);

            entity.Property(l => l.LinkType).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<WorkItemTag>(entity =>
        {
            entity.ToTable("WorkItemTags", "work");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.WorkItemId, t.Name }).IsUnique();
            entity.HasIndex(t => t.Name);

            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);
        });

        // ----------------------------------------------------------------
        // RBAC Module — schema: iam
        // ----------------------------------------------------------------
        modelBuilder.Entity<RoleAssignment>(entity =>
        {
            entity.ToTable("RoleAssignments", "iam");
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_RoleAssignment_ExactlyOneScope",
                @"(CASE WHEN ""OrganizationId"" IS NOT NULL THEN 1 ELSE 0 END +
                CASE WHEN ""ProjectId"" IS NOT NULL THEN 1 ELSE 0 END +
                CASE WHEN ""TeamId"" IS NOT NULL THEN 1 ELSE 0 END) = 1"
            ));
            entity.HasKey(r => r.Id);

            // Uniqueness of (user, role, scope target) is enforced by three *partial* unique
            // indexes created in raw SQL by the HardenRoleAssignmentAndOrgMembership migration
            // (IX_RoleAssignments_Unique_Org / _Project / _Team, each filtered to
            // "WHERE <scope column> IS NOT NULL"). They are deliberately not modelled here:
            // a plain composite HasIndex over the three nullable columns would be useless,
            // because Postgres treats NULLs as distinct and would accept unlimited duplicates.
            //
            // Two more constraints live in raw SQL for the same reason, added by the
            // Stage2_TeamPositions migration:
            //   CK_RoleAssignment_RoleMatchesScope          a role must be one that means something
            //                                               at the scope it is held (see
            //                                               RolePermissions.IsValidAt)
            //   IX_RoleAssignments_OneHolderPerTeamPosition  one TeamLead / ScrumMaster /
            //                                               ProductOwner per team, partial on
            //                                               "TeamId" IS NOT NULL
            entity.Property(r => r.PrincipalType).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(r => new { r.Scope, r.ProjectId, r.TeamId, r.OrganizationId });
            entity.HasIndex(r => r.UserId);
            entity.HasIndex(r => r.TeamId);
            entity.HasIndex(r => r.ProjectId);
            entity.HasIndex(r => r.OrganizationId);

            // Store enum as its name string (e.g. "OrgAdmin"), not the numeric value ("10").
            // ValueConverter ensures EF uses Enum.GetName / Enum.Parse rather than (int) cast.
            entity.Property(r => r.Role)
                .HasMaxLength(30)
                .HasConversion(
                    v => v.ToString(),
                    v => (RoleType)Enum.Parse(typeof(RoleType), v));

            entity.Property(r => r.Scope)
                .HasMaxLength(20)
                .HasConversion(
                    v => v.ToString(),
                    v => (RoleScope)Enum.Parse(typeof(RoleScope), v));
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(r => r.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(r => r.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Team>()
                .WithMany()
                .HasForeignKey(r => r.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
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

            entity.Property(p => p.Key).IsRequired().HasMaxLength(10);

            // Unique per organization, and the lookup every git binding makes: a reference names a
            // key, and the key has to resolve to exactly one project.
            entity.HasIndex(p => new { p.OrganizationId, p.Key }).IsUnique();

            entity.Property(p => p.Slug).IsRequired().HasMaxLength(60);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(500);

            entity.HasOne(p => p.AssignedTeam)
                .WithMany(t => t.AssignedProjects)
                .HasForeignKey(p => p.AssignedTeamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("Teams", "org");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.OrganizationId, t.Name }).IsUnique();
            entity.HasIndex(t => t.IsActive);

            entity.Property(t => t.Name).IsRequired().HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(500);

            entity.HasMany(t => t.Members)
                .WithOne(m => m.Team)
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(t => t.OrganizationId)
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
        // Sprints / Boards Module — schema: plan
        // ----------------------------------------------------------------
        modelBuilder.Entity<Sprint>(entity =>
        {
            entity.ToTable("Sprints", "plan");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.ProjectId);
            entity.HasIndex(s => new { s.ProjectId, s.Number }).IsUnique();
            entity.HasIndex(s => s.Status);

            // "The active sprint for this project" runs on every board render.
            entity.HasIndex(s => new { s.ProjectId, s.Status });

            entity.Property(s => s.Goal).HasMaxLength(500);
            entity.Property(s => s.Status)
                .HasMaxLength(20)
                .HasConversion(
                    v => v.ToString(),
                    v => (SprintStatus)Enum.Parse(typeof(SprintStatus), v));

            entity.HasMany(s => s.SprintWorkItems)
                .WithOne(sw => sw.Sprint)
                .HasForeignKey(sw => sw.SprintId)
                .OnDelete(DeleteBehavior.Cascade);

            // A sprint belongs to exactly one project, and SprintScopeResolver reads ProjectId to
            // decide whose grants apply to it. Without the key that is a convention: a sprint whose
            // ProjectId named something that was not a project would resolve to a scope nobody holds
            // and deny everyone, which is exactly the failure the team → project rename produced.
            // Cascade because a sprint has no meaning once its project is gone.
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(s => s.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SprintWorkItem>(entity =>
        {
            entity.ToTable("SprintWorkItems", "plan");
            entity.HasKey(sw => sw.Id);
            entity.HasIndex(sw => new { sw.SprintId, sw.WorkItemId }).IsUnique();
            entity.HasIndex(sw => new { sw.SprintId, sw.Rank }).IsUnique();
            entity.HasIndex(sw => sw.WorkItemId);

            // Backlog and board reads want one sprint's entries already in display order. Rank is
            // the sort key now; the Position index stays for the legacy column while anything still
            // reads it.
            entity.HasIndex(sw => new { sw.SprintId, sw.Position });
        });

        modelBuilder.Entity<Board>(entity =>
        {
            entity.ToTable("Boards", "plan");
            entity.HasKey(b => b.Id);
            entity.HasIndex(b => b.ProjectId).IsUnique(); // one board per project

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(b => b.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(b => b.Name).IsRequired().HasMaxLength(100);

            entity.HasMany(b => b.Columns)
                .WithOne(c => c.Board)
                .HasForeignKey(c => c.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BoardColumn>(entity =>
        {
            entity.ToTable("BoardColumns", "plan");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.BoardId);

            entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
            entity.Property(c => c.MappedState).IsRequired().HasMaxLength(20);
        });

        // ----------------------------------------------------------------
        // Activity Module — schema: activity
        // ----------------------------------------------------------------
        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.ToTable("ActivityLogs", "activity");
            entity.HasKey(a => a.Id);

            // Both feeds read "newest first within a set of organizations" and nothing else, so
            // one composite index serves them: the org feed probes a single value, the workspace
            // feed a small IN-list, and either way the sort comes out of the index.
            entity.HasIndex(a => new { a.OrganizationId, a.OccurredAt })
                .IsDescending(false, true);

            // The idempotency key. A redelivered outbox message carries the same EventId, and this
            // index is what turns "write it twice" into a no-op instead of a duplicate feed line.
            entity.HasIndex(a => a.EventId).IsUnique();
            entity.HasIndex(a => a.ProjectId);
            entity.HasIndex(a => a.TeamId);
            entity.HasIndex(a => a.EntityId);

            entity.Property(a => a.EntityTitle).IsRequired().HasMaxLength(255);
            entity.Property(a => a.FieldName).HasMaxLength(100);
            entity.Property(a => a.OldValue).HasMaxLength(1000);
            entity.Property(a => a.NewValue).HasMaxLength(1000);

            // Stored as names for the same reason RoleAssignment.Role is — a readable audit table
            // survives enum renumbering, and nothing compares these ordinally.
            entity.Property(a => a.EntityType)
                .IsRequired()
                .HasMaxLength(30)
                .HasConversion(
                    v => v.ToString(),
                    v => (ActivityEntityType)Enum.Parse(typeof(ActivityEntityType), v));

            entity.Property(a => a.Verb)
                .IsRequired()
                .HasMaxLength(30)
                .HasConversion(
                    v => v.ToString(),
                    v => (ActivityVerb)Enum.Parse(typeof(ActivityVerb), v));

            // No foreign keys to the subject rows on purpose: activity outlives what it describes,
            // and a cascade from a deleted project must not erase the record that it was deleted.
        });

        // ----------------------------------------------------------------
        // Backlog Module — schema: plan
        // ----------------------------------------------------------------
        modelBuilder.Entity<BacklogItem>(entity =>
        {
            entity.ToTable("BacklogItems", "plan");
            entity.HasKey(b => b.Id);
            entity.HasIndex(b => b.ProjectId);
            entity.HasIndex(b => b.WorkItemId);
            entity.HasIndex(b => b.SprintId);
            entity.HasIndex(b => new { b.ProjectId, b.WorkItemId }).IsUnique();
            entity.HasIndex(b => new { b.ProjectId, b.Rank });
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
        // ----------------------------------------------------------------
        // Shared kernel — schema: kernel
        // ----------------------------------------------------------------
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", "kernel");
            entity.HasKey(m => m.Sequence);

            // Database-generated so the ordering authority is the database, not the application.
            // Several instances write concurrently; only the sequence can order them.
            entity.Property(m => m.Sequence).ValueGeneratedOnAdd();

            entity.HasIndex(m => m.EventId).IsUnique();

            // Partial: the dispatcher only ever asks for undelivered rows, and once the table has
            // months of delivered history a full index on DispatchedAt would be mostly dead weight.
            entity.HasIndex(m => m.Sequence)
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_OutboxMessages_Undispatched");

            entity.Property(m => m.EventType).IsRequired().HasMaxLength(200);
            entity.Property(m => m.Payload).IsRequired().HasColumnType("jsonb");

            // GIN so "which messages touched this topic?" is an index lookup on array containment.
            // A btree cannot answer that — it would degrade into a scan as the table grows, which
            // is exactly the query a reconnecting client makes.
            entity.Property(m => m.Topics).HasColumnType("text[]");
            entity.HasIndex(m => m.Topics).HasMethod("gin");
            entity.Property(m => m.LastError).HasMaxLength(2000);
        });

        // ----------------------------------------------------------------
        // Notifications
        // ----------------------------------------------------------------
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications", "notify");
            entity.HasKey(n => n.Id);

            entity.Property(n => n.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(n => n.Reference).IsRequired().HasMaxLength(30);
            entity.Property(n => n.Title).IsRequired().HasMaxLength(300);
            entity.Property(n => n.Detail).HasMaxLength(500);
            entity.Property(n => n.ActorName).IsRequired().HasMaxLength(200);

            // The bell's own query: one recipient, newest first.
            entity.HasIndex(n => new { n.RecipientId, n.CreatedAt });

            // The badge. Partial, because the count only ever asks about unread rows and the table
            // is mostly read ones within a week of going live.
            entity.HasIndex(n => n.RecipientId)
                .HasFilter("\"ReadAt\" IS NULL")
                .HasDatabaseName("IX_Notifications_Unread");

            // What makes at-least-once outbox delivery safe: a redelivered event finds the row
            // already there. Per recipient, because one event legitimately notifies several people.
            entity.HasIndex(n => new { n.EventId, n.RecipientId }).IsUnique();
        });

        modelBuilder.Entity<WorkItemWatcher>(entity =>
        {
            entity.ToTable("WorkItemWatchers", "notify");
            entity.HasKey(w => w.Id);

            // One row per person per item — the row records a decision, including the decision to
            // stop, so it must not accumulate.
            entity.HasIndex(w => new { w.WorkItemId, w.UserId }).IsUnique();

            // Fanning out to watchers, which happens on every state change and every comment.
            entity.HasIndex(w => w.WorkItemId).HasFilter("\"IsWatching\"");

            entity.HasIndex(w => w.UserId);
        });

        // ----------------------------------------------------------------
        // Jobs — long-running work. See Shared/Kernel/Jobs/Job.cs for why this is
        // separate from the outbox.
        // ----------------------------------------------------------------
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("Jobs", "kernel");
            entity.HasKey(j => j.Sequence);
            entity.Property(j => j.Sequence).ValueGeneratedOnAdd();

            // The idempotency key. Enqueueing the same work twice is a no-op rather than a
            // duplicate, which is what lets a webhook redelivery be accepted safely.
            entity.HasIndex(j => j.JobId).IsUnique();

            entity.Property(j => j.JobType).IsRequired().HasMaxLength(100);
            entity.Property(j => j.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(j => j.LeasedBy).HasMaxLength(200);
            entity.Property(j => j.LastError).HasMaxLength(2000);

            // Partial, and ordered exactly as the claim query orders: the worker only ever asks for
            // outstanding rows, so once the table holds months of completed history a full index
            // would be mostly dead weight.
            entity.HasIndex(j => new { j.Priority, j.Sequence })
                .HasFilter("\"CompletedAt\" IS NULL AND \"DeadAt\" IS NULL")
                .HasDatabaseName("IX_Jobs_Runnable");
        });

        // ----------------------------------------------------------------
        // GitSync module
        // ----------------------------------------------------------------
        modelBuilder.Entity<GitProviderInstallation>(entity =>
        {
            entity.ToTable("Installations", "git");
            entity.HasKey(i => i.Id);

            entity.Property(i => i.Provider).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Verification).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.ExternalId).IsRequired().HasMaxLength(200);
            entity.Property(i => i.AccountName).IsRequired().HasMaxLength(200);
            entity.Property(i => i.WebhookSecret).IsRequired().HasMaxLength(200);
            entity.Property(i => i.EndpointToken).IsRequired().HasMaxLength(100);

            // How every inbound webhook finds its installation, so it must be an index lookup
            // rather than a scan — and unique, because two installations sharing a token would make
            // the answer ambiguous.
            entity.HasIndex(i => new { i.Provider, i.EndpointToken }).IsUnique();

            // One connection per account per provider per organization.
            entity.HasIndex(i => new { i.OrganizationId, i.Provider, i.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<RepositoryLink>(entity =>
        {
            entity.ToTable("RepositoryLinks", "git");
            entity.HasKey(l => l.Id);

            entity.Property(l => l.RepositoryExternalId).IsRequired().HasMaxLength(200);
            entity.Property(l => l.RepositoryName).IsRequired().HasMaxLength(400);
            entity.Property(l => l.DefaultBranch).IsRequired().HasMaxLength(200);

            // The lookup every delivery makes: which projects does this repository feed?
            entity.HasIndex(l => new { l.InstallationId, l.RepositoryExternalId });
            entity.HasIndex(l => l.ProjectId);

            // A repository may serve several projects (monorepos), but not the same one twice.
            entity.HasIndex(l => new { l.InstallationId, l.RepositoryExternalId, l.ProjectId })
                .IsUnique();

            entity.HasOne(l => l.Installation)
                .WithMany()
                .HasForeignKey(l => l.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("WebhookDeliveries", "git");
            entity.HasKey(d => d.Id);

            entity.Property(d => d.Provider).HasConversion<string>().HasMaxLength(30);
            entity.Property(d => d.Verification).HasConversion<string>().HasMaxLength(30);
            entity.Property(d => d.ProviderDeliveryId).IsRequired().HasMaxLength(200);
            entity.Property(d => d.EventName).IsRequired().HasMaxLength(100);
            entity.Property(d => d.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(d => d.Outcome).HasMaxLength(2000);

            // The idempotency key for ingest. A provider redelivering reuses its original id, so
            // this is what makes accepting a redelivery a no-op instead of duplicate work.
            entity.HasIndex(d => new { d.Provider, d.ProviderDeliveryId })
                .IsUnique()
                .HasDatabaseName(GitSchema.DeliveryUniqueIndex);

            entity.HasIndex(d => new { d.InstallationId, d.CreatedAt });

            entity.HasOne(d => d.Installation)
                .WithMany()
                .HasForeignKey(d => d.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
