using BoardSync.Api.Data;
using BoardSync.Api.Modules.Notifications.Models;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Notifications.Repositories.Implementations;

/// <inheritdoc />
public class NotificationWriter : INotificationWriter
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<NotificationWriter> _logger;

    public NotificationWriter(BoardSyncDbContext context, ILogger<NotificationWriter> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task WriteAsync(NotificationDraft draft, CancellationToken ct = default)
    {
        var recipients = draft.Recipients.Distinct().Where(id => id != Guid.Empty).ToList();

        if (recipients.Count == 0) return;

        // The idempotency check. A redelivered outbox message finds these rows already written and
        // adds nothing — which is what makes at-least-once delivery safe here.
        var alreadyTold = await _context.Notifications
            .Where(n => n.EventId == draft.EventId && recipients.Contains(n.RecipientId))
            .Select(n => n.RecipientId)
            .ToListAsync(ct);

        var pending = recipients.Except(alreadyTold).ToList();

        if (pending.Count == 0) return;

        var actorName = draft.ActorName
            ?? (draft.ActorId is { } actor ? await ActorNameAsync(actor, ct) : string.Empty);

        foreach (var recipient in pending)
        {
            _context.Notifications.Add(new Notification
            {
                RecipientId = recipient,
                Type = draft.Type,
                EventId = draft.EventId,
                EntityId = draft.Item.Id,
                ProjectId = draft.Item.ProjectId,
                Reference = draft.Item.Reference,
                Title = draft.Title,
                Detail = Truncate(draft.Detail, 500),
                ActorId = draft.ActorId,
                ActorName = actorName
            });
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogDebug("Wrote {Count} {Type} notification(s) for event {EventId}.",
            pending.Count, draft.Type, draft.EventId);
    }

    public async Task<NotifiableItem?> DescribeAsync(Guid workItemId, CancellationToken ct = default)
    {
        var found = await _context.WorkItems
            .Where(w => w.Id == workItemId)
            .Join(_context.Projects, w => w.ProjectId, p => p.Id,
                (w, p) => new { w.Id, w.ProjectId, w.Number, w.Title, w.AssigneeId, p.Key })
            .FirstOrDefaultAsync(ct);

        return found is null
            ? null
            : new NotifiableItem(
                found.Id, found.ProjectId, $"{found.Key}-{found.Number}", found.Title, found.AssigneeId);
    }

    public async Task<IReadOnlyList<Guid>> GetWatchersAsync(Guid workItemId, CancellationToken ct = default) =>
        await _context.WorkItemWatchers
            .Where(w => w.WorkItemId == workItemId && w.IsWatching)
            .Select(w => w.UserId)
            .ToListAsync(ct);

    public async Task WatchAsync(
        Guid workItemId, Guid projectId, Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return;

        var existing = await _context.WorkItemWatchers
            .FirstOrDefaultAsync(w => w.WorkItemId == workItemId && w.UserId == userId, ct);

        // Somebody who deliberately stopped watching stays stopped. Implicit watching is a
        // convenience, and a convenience that overrides a stated preference is an annoyance.
        if (existing is not null) return;

        _context.WorkItemWatchers.Add(new WorkItemWatcher
        {
            WorkItemId = workItemId,
            ProjectId = projectId,
            UserId = userId,
            CreatedBy = userId
        });

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateWatcher(ex))
        {
            // Two handlers for the same event racing to auto-watch the same person. The unique index
            // settles it and the loser has nothing to do.
            _context.ChangeTracker.Clear();
        }
    }

    public async Task SetWatchingAsync(
        Guid workItemId, Guid projectId, Guid userId, bool watching, CancellationToken ct = default)
    {
        var existing = await _context.WorkItemWatchers
            .FirstOrDefaultAsync(w => w.WorkItemId == workItemId && w.UserId == userId, ct);

        if (existing is null)
        {
            _context.WorkItemWatchers.Add(new WorkItemWatcher
            {
                WorkItemId = workItemId,
                ProjectId = projectId,
                UserId = userId,
                IsWatching = watching,
                CreatedBy = userId
            });
        }
        else
        {
            existing.IsWatching = watching;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct);
    }

    public Task<bool> IsWatchingAsync(Guid workItemId, Guid userId, CancellationToken ct = default) =>
        _context.WorkItemWatchers.AnyAsync(
            w => w.WorkItemId == workItemId && w.UserId == userId && w.IsWatching, ct);

    /// <remarks>
    /// An actor that matches no user is an integration — the id is a
    /// <c>GitProviderInstallation</c>, not a person — so it is named by its provider rather than
    /// left blank. "moved to Awaiting QA by GitHub" is the sentence a reader needs.
    /// </remarks>
    public async Task<string> ActorNameAsync(Guid actorId, CancellationToken ct = default)
    {
        if (actorId == Guid.Empty) return string.Empty;

        var user = await _context.Users
            .Where(u => u.Id == actorId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);

        if (user is not null) return user;

        var installation = await _context.GitProviderInstallations
            .Where(i => i.Id == actorId)
            .Select(i => i.Provider)
            .FirstOrDefaultAsync(ct);

        return installation.ToString();
    }

    public async Task<string?> CommentPreviewAsync(Guid commentId, CancellationToken ct = default)
    {
        var body = await _context.WorkItemComments
            .Where(c => c.Id == commentId)
            .Select(c => c.Body)
            .FirstOrDefaultAsync(ct);

        if (body is null) return null;

        // First line only. A bell row is one line high, and a comment's opening sentence is what
        // tells somebody whether to open it.
        var firstLine = body.Split('\n', 2)[0].Trim();

        return Truncate(firstLine, 200);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static bool IsDuplicateWatcher(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
