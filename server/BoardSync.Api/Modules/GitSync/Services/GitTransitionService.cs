using BoardSync.Api.Modules.GitSync.Domain;
using BoardSync.Api.Data;
using BoardSync.Api.Modules.GitSync.Providers;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.GitSync.Services;

/// <summary>What happened to one work item as a result of a git event.</summary>
/// <param name="Reference">What the developer typed, e.g. <c>BS-142</c>.</param>
/// <param name="From">The state it was in.</param>
/// <param name="To">The state it moved to, or <c>From</c> when nothing happened.</param>
/// <param name="Skipped">Why it did not move, or null when it did.</param>
public readonly record struct TransitionResult(
    string Reference, WorkItemState From, WorkItemState To, string? Skipped)
{
    public bool Moved => Skipped is null;
}

/// <summary>Moves work items in response to git events.</summary>
public interface IGitTransitionService
{
    Task<IReadOnlyList<TransitionResult>> ApplyAsync(
        NormalizedGitEvent gitEvent,
        IReadOnlyList<BoundWorkItem> bound,
        Guid installationId,
        string defaultBranch,
        CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// This is the product's central mechanism: the thing that means nobody drags cards. Three
/// invariants make it trustworthy, and each exists because of a specific way automation like this
/// goes wrong.
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Monotonic.</b> A git event never moves an item backwards. Webhooks arrive out of order
///     routinely — a retried push landing after the pull request it preceded — and without this a
///     late delivery would drag a merged item back to Active.
///   </description></item>
///   <item><description>
///     <b>A human wins.</b> If a person changed the state after the event happened, the event is
///     recorded and does not move anything. The board is derived from git, but somebody who
///     deliberately overrode it knew something git did not.
///   </description></item>
///   <item><description>
///     <b><c>Resolved</c> is the ceiling</b> — and not because this method stops there. The
///     integration principal does not hold <c>workitem:verify</c>, so the permission check inside
///     <c>WorkItemService</c> refuses <c>Closed</c> whatever this asks for. The gate survives a bug
///     here.
///   </description></item>
/// </list>
/// </remarks>
public class GitTransitionService : IGitTransitionService
{
    private readonly BoardSyncDbContext _context;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<GitTransitionService> _logger;

    public GitTransitionService(
        BoardSyncDbContext context,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<GitTransitionService> logger)
    {
        _context = context;
        _rbac = rbac;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TransitionResult>> ApplyAsync(
        NormalizedGitEvent gitEvent,
        IReadOnlyList<BoundWorkItem> bound,
        Guid installationId,
        string defaultBranch,
        CancellationToken ct = default)
    {
        var target = TargetStateFor(gitEvent, defaultBranch);

        if (target is not { } desired || bound.Count == 0) return [];

        var attributedTo = await ResolveActorAsync(gitEvent, ct);
        var results = new List<TransitionResult>(bound.Count);

        foreach (var item in bound)
            results.Add(await ApplyOneAsync(item, desired, gitEvent, installationId, attributedTo, ct));

        if (results.Any(r => r.Moved))
            await _context.SaveChangesAsync(ct);

        return results;
    }

    /// <summary>
    /// Which state this kind of event means, or null when it means nothing on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A merge only resolves when it lands on the default branch.</b> Merging a feature branch
    /// into another feature branch is ordinary work, not completion, and treating it as completion
    /// would mark items done that are nowhere near it.
    /// </para>
    /// <para>
    /// A pull request closed unmerged sends the work back to Active: the branch is still there and
    /// the work is still someone's, it just did not land.
    /// </para>
    /// </remarks>
    private static WorkItemState? TargetStateFor(NormalizedGitEvent gitEvent, string defaultBranch) =>
        gitEvent.Kind switch
        {
            // Someone has started. Only ever promotes out of New — see the monotonic rule.
            GitEventKind.BranchCreated or GitEventKind.Push => WorkItemState.Active,

            GitEventKind.PullRequestOpened => WorkItemState.InReview,

            GitEventKind.PullRequestMerged =>
                string.Equals(gitEvent.TargetBranch, defaultBranch, StringComparison.OrdinalIgnoreCase)
                    ? WorkItemState.Resolved
                    : null,

            GitEventKind.PullRequestClosed => WorkItemState.Active,

            _ => null
        };

    private async Task<TransitionResult> ApplyOneAsync(
        BoundWorkItem bound,
        WorkItemState desired,
        NormalizedGitEvent gitEvent,
        Guid installationId,
        Guid? attributedTo,
        CancellationToken ct)
    {
        var item = await _context.WorkItems.FirstOrDefaultAsync(w => w.Id == bound.WorkItemId, ct);

        if (item is null)
            return new TransitionResult(bound.Reference.ToString(), default, default, "no longer exists");

        var from = item.State;

        if (from == desired)
            return new TransitionResult(bound.Reference.ToString(), from, from, "already there");

        // Invariant 1 — monotonic.
        if (TransitionPath.Rank(desired) < TransitionPath.Rank(from))
        {
            return new TransitionResult(bound.Reference.ToString(), from, from,
                $"would move backwards from {from}");
        }

        /*
         * The route forward, which is not always one step.
         *
         * A pull request opening on an item still in New used to be refused outright — New reaches
         * only Active in one hop — and because the refusal left the item in New, every later event
         * for it failed the same way. The item was stranded permanently by one missed push: a
         * branch pushed before the repository was linked, a work item created after the branch, or
         * a delivery that failed while the integration had no grant.
         *
         * The monotonic invariant forbids moving *backwards*; it never said an event may not carry
         * an item more than one state forward. An event saying "this is in review" is also evidence
         * that it was started.
         */
        var path = TransitionPath.Forward(from, desired);

        if (path.Count == 0)
        {
            return new TransitionResult(bound.Reference.ToString(), from, from,
                $"{from} → {desired} is not reachable");
        }

        // Invariant 2 — a human wins.
        if (await HumanChangedStateSinceAsync(item.Id, gitEvent.OccurredAt.UtcDateTime, ct))
        {
            return new TransitionResult(bound.Reference.ToString(), from, from,
                "a person changed it after this event happened");
        }

        // Invariant 3 — the ceiling is a permission, not a rule here. Asking the evaluator rather
        // than assuming means a widened Integration role would be caught by the QA gate's own tests
        // rather than by this method silently allowing more.
        /*
         * Every hop is checked, not just the last one. Checking only the destination would let a
         * walk pass through a transition the principal may not make — and the whole point of asking
         * the evaluator rather than assuming is that a widened Integration role gets caught by the
         * QA gate's own tests instead of silently allowing more.
         *
         * A refusal anywhere refuses the whole walk. A partial move would leave the item somewhere
         * nobody's event described, with an outcome line that no longer matches where it landed.
         */
        var previous = from;

        foreach (var step in path)
        {
            var required = WorkItemStateMachine.RequiredPermission(previous, step);

            if (!await _rbac.HasPermissionAsync(
                    installationId, required, RoleScope.Project, item.ProjectId, ct))
            {
                _logger.LogWarning(
                    "Installation {InstallationId} may not move {Reference} from {From} to {To} ({Permission}).",
                    installationId, bound.Reference, previous, step, required);

                return new TransitionResult(bound.Reference.ToString(), from, from,
                    $"the integration does not hold {required}");
            }

            previous = step;
        }

        /*
         * Nothing is written until every hop has been permitted, so a refusal halfway leaves the
         * change tracker untouched rather than relying on the caller not to save.
         */
        previous = from;

        foreach (var step in path)
        {
            _context.WorkItemHistory.Add(new WorkItemHistory
            {
                WorkItemId = item.Id,
                ProjectId = item.ProjectId,
                ChangedBy = installationId,
                ActorType = PrincipalType.Integration,
                AttributedToUserId = attributedTo,
                FieldName = "State",
                OldValue = previous.ToString(),
                NewValue = step.ToString(),
                CreatedBy = installationId
            });

            // Before the save, so the event and the change it describes commit together — the
            // ordering that silently dropped every work item event when it was the other way round.
            _eventBus.Enqueue(
                new WorkItemStateChanged(item.Id, item.ProjectId, previous, step, installationId));

            previous = step;
        }

        item.State = desired;
        item.UpdatedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "{Reference} moved {From} → {To} by {Repository} ({Kind}).",
            bound.Reference, from, desired, gitEvent.RepositoryName, gitEvent.Kind);

        return new TransitionResult(bound.Reference.ToString(), from, desired, null);
    }

    /// <summary>
    /// Whether a person changed this item's state after the moment the git event describes.
    /// </summary>
    /// <remarks>
    /// Reads the audit trail rather than <c>UpdatedAt</c>, which any edit touches — a retitle is not
    /// an override. <c>ActorType</c> is what makes this answerable at all: without it, the
    /// integration's own earlier transition would look like a human's and block every subsequent
    /// event.
    /// </remarks>
    private Task<bool> HumanChangedStateSinceAsync(Guid workItemId, DateTime since, CancellationToken ct) =>
        _context.WorkItemHistory.AnyAsync(
            h => h.WorkItemId == workItemId
                 && h.FieldName == "State"
                 && h.ActorType == PrincipalType.User
                 && h.CreatedAt > since,
            ct);

    /// <summary>
    /// The BoardSync user behind a git actor, when there is one.
    /// </summary>
    /// <remarks>
    /// <b>Attribution only.</b> Finding a match changes nothing about what the integration may do; it
    /// only lets the feed say "by GitHub (Ada Lovelace)" instead of "by GitHub". No match is the
    /// normal case for external contributors and bots, and is not worth a warning.
    /// </remarks>
    private async Task<Guid?> ResolveActorAsync(NormalizedGitEvent gitEvent, CancellationToken ct)
    {
        var email = gitEvent.Commits.FirstOrDefault(c => !c.IsMerge)?.AuthorEmail
                    ?? gitEvent.Actor.Email;

        if (string.IsNullOrWhiteSpace(email)) return null;

        return await _context.Users
            .Where(u => u.Email.ToLower() == email.ToLower() && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }
}
