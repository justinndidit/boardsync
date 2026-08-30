using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Services;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Scheduling;

/// <summary>
/// Starts sprints when they begin and closes them when they end.
/// </summary>
/// <remarks>
/// <para>
/// A sprint's dates meant nothing until this existed. Nothing ran on a schedule, so a sprint stayed
/// <c>Planning</c> past its start indefinitely, and an <c>Active</c> sprint sat open past its end
/// with its unfinished work still inside it — in no backlog, in no other sprint, and on no board.
/// The board even showed such a sprint as "Closed", because the client derived a status from the
/// dates while the stored one said otherwise.
/// </para>
/// <para>
/// <b>Closing sends unfinished work to the backlog, never to the next sprint.</b> There is nobody to
/// ask at two in the morning, and the two destinations are different statements: returning it says
/// "not committing to this yet", which is the reversible one. Carrying it forward would silently
/// inflate the next sprint's commitment with work nobody planned, and a team would find out from a
/// velocity chart.
/// </para>
/// <para>
/// Everything it does goes through <see cref="ISprintService"/> — the same calls a person makes.
/// A scheduler with its own path into the domain is a second set of rules waiting to disagree with
/// the first.
/// </para>
/// </remarks>
public class SprintScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SprintScheduler> _logger;

    /// <summary>
    /// How often the clock is checked.
    /// </summary>
    /// <remarks>
    /// A minute. Sprint boundaries are a human-scale event — nobody notices a sprint closing 40
    /// seconds late — and a tighter loop would spend the day asking a question whose answer changes
    /// twice a fortnight.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public SprintScheduler(
        IServiceScopeFactory scopeFactory,
        ILogger<SprintScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Sprint scheduler started; checking every {Seconds}s.", Interval.TotalSeconds);

        using var timer = new PeriodicTimer(Interval);

        // Once immediately, so a restart catches boundaries missed while the process was down.
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                /*
                 * Swallowed so one bad sprint cannot stop the loop for every other team. The next
                 * pass retries it — the work is idempotent because it is driven by status, and a
                 * sprint this failed on is still Active with its end date still past.
                 */
                _logger.LogError(ex, "Sprint scheduler pass failed; retrying next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(
        PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One pass of the clock: start what is due, close what is over.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive a pass deterministically rather than waiting a minute for the
    /// timer, and so an operator can be told "it runs on a minute" and mean it. Idempotent: both
    /// halves are driven by status, so a pass that finds nothing due does nothing.
    /// </remarks>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await StartDueSprintsAsync(ct);
        await CloseFinishedSprintsAsync(ct);
    }

    /// <summary>Planning sprints whose start time has arrived.</summary>
    private async Task StartDueSprintsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<BoardSyncDbContext>();

        var sprints = scope.ServiceProvider
            .GetRequiredService<ISprintService>();

        var now = DateTime.UtcNow;

        var due = await context.Sprints
            .Where(s => s.Status == SprintStatus.Planning && s.StartDate <= now)
            .OrderBy(s => s.StartDate)
            .Select(s => new { s.Id, s.Number, s.TeamId })
            .ToListAsync(ct);

        foreach (var sprint in due)
        {
            /*
             * One active sprint per team is the rule, and the service enforces it. A team whose
             * previous sprint is still open gets a warning rather than a silent skip: the sprint
             * stays in Planning, which is visible, and somebody has to close the old one.
             */
            var alreadyRunning = await context.Sprints.AnyAsync(
                s => s.TeamId == sprint.TeamId && s.Status == SprintStatus.Active, ct);

            if (alreadyRunning)
            {
                _logger.LogWarning(
                    "Sprint {Number} for team {TeamId} was due to start and did not: the team "
                    + "already has an active sprint. Close it and this one starts on the next pass.",
                    sprint.Number, sprint.TeamId);

                continue;
            }

            await sprints.UpdateStatusAsync(
                sprint.Id, SprintStatus.Active, SystemActor, ct);

            _logger.LogInformation(
                "Sprint {Number} started on schedule.", sprint.Number);
        }
    }

    /// <summary>Active sprints whose end time has passed.</summary>
    private async Task CloseFinishedSprintsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<BoardSyncDbContext>();

        var sprints = scope.ServiceProvider
            .GetRequiredService<ISprintService>();

        var now = DateTime.UtcNow;

        var over = await context.Sprints
            .Where(s => s.Status == SprintStatus.Active && s.EndDate <= now)
            .OrderBy(s => s.EndDate)
            .Select(s => new { s.Id, s.Number })
            .ToListAsync(ct);

        foreach (var sprint in over)
        {
            var result = await sprints.CloseAsync(
                sprint.Id,
                new CloseSprintRequest
                {
                    IncompleteItemsDestination =
                        IncompleteItemsDestination.ReturnToBacklog,
                },
                SystemActor,
                ct);

            _logger.LogInformation(
                "Sprint {Number} closed on schedule: {Completed} finished, {Incomplete} returned "
                + "to the backlog.",
                sprint.Number, result.CompletedItemCount, result.IncompleteItemCount);
        }
    }

    /// <summary>
    /// Who the scheduler acts as.
    /// </summary>
    /// <remarks>
    /// An empty id rather than a borrowed one. History and activity entries record who changed
    /// something, and attributing a clock to the person who happened to create the sprint would put
    /// a decision in their name that they did not make.
    /// </remarks>
    private static readonly Guid SystemActor = Guid.Empty;
}
