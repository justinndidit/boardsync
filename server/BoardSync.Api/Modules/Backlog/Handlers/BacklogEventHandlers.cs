using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Modules.Backlog.Services;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.Backlog.Handlers;

/// <summary>
/// Gives every new work item a place in the product backlog.
/// </summary>
/// <remarks>
/// <para>
/// <c>BacklogItem</c> describes itself as "one row per work item", and it was not: only
/// <c>POST /projects/{id}/backlog</c> ever created one, so an item created from the Work Items page
/// or a board existed in no backlog at all. It was on the Work Items list and on a board with no
/// sprint selected, and absent from the one screen whose whole job is "what should we do next" —
/// recoverable only by somebody knowing to go and add it back.
/// </para>
/// <para>
/// A subscriber rather than a call inside <c>WorkItemService.CreateAsync</c>. The WorkItems module
/// has no reason to know the Backlog module exists, and this is the edge the outbox is for — the
/// same way Activity and Notifications already learn about work items. It costs eventual
/// consistency measured in milliseconds, which is the same deal the activity feed takes.
/// </para>
/// <para>
/// <b>The rank is what this is really creating.</b> The row is deliberately kept when an item is
/// pulled into a sprint, so an item that comes back out returns to where it was in the order rather
/// than to the bottom. An item that never had a row has no position to return to.
/// </para>
/// </remarks>
public class BacklogEventHandlers : IEventHandler<WorkItemCreated>
{
    private readonly IBacklogService _backlog;
    private readonly ILogger<BacklogEventHandlers> _logger;

    public BacklogEventHandlers(
        IBacklogService backlog,
        ILogger<BacklogEventHandlers> logger)
    {
        _backlog = backlog;
        _logger = logger;
    }

    public async Task HandleAsync(WorkItemCreated e, CancellationToken ct = default)
    {
        try
        {
            /*
             * Idempotent on the service's side, so a redelivered message adds nothing and an item
             * that was created *through* the backlog endpoint is unaffected.
             *
             * Attributed to whoever created the item: they decided it was worth doing, which is
             * exactly what a backlog entry records.
             */
            await _backlog.AddAsync(
                e.ProjectId,
                new AddToBacklogRequest { WorkItemId = e.WorkItemId },
                e.CreatedByUserId,
                ct);
        }
        catch (Exception ex)
        {
            /*
             * Swallowed on purpose. The work item exists and is the thing the user asked for;
             * failing here would retry the whole message and re-run every other subscriber with it.
             * The cost of losing this is one item with no rank, which the backlog's own "add"
             * recovers — so it is logged loudly and left alone.
             */
            _logger.LogError(ex,
                "Work item {WorkItemId} was created but could not be added to the backlog of " +
                "project {ProjectId}. It will not appear in the backlog until somebody adds it.",
                e.WorkItemId, e.ProjectId);
        }
    }
}
