using System.Text.Json;
using BoardSync.Api.Data;

namespace BoardSync.Api.Shared.Kernel.Jobs;

/// <inheritdoc />
/// <remarks>
/// Scoped, sharing the request's <c>DbContext</c> with every repository — that shared instance is
/// what makes the job and the rows it describes land in one transaction. It does no I/O of its own.
/// </remarks>
public class JobQueue : IJobQueue
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<JobQueue> _logger;

    public JobQueue(BoardSyncDbContext context, ILogger<JobQueue> logger)
    {
        _context = context;
        _logger = logger;
    }

    public void Enqueue<TPayload>(
        Guid jobId,
        TPayload payload,
        int priority = JobPriority.Normal,
        DateTime? visibleAt = null)
        where TPayload : IJobPayload
    {
        // Serialized against the concrete type so a caller holding an interface reference cannot
        // silently drop the payload — the same trap OutboxEventBus documents.
        var json = JsonSerializer.Serialize(payload, payload!.GetType(), SerializerOptions);

        _context.Jobs.Add(new Job
        {
            JobId = jobId,
            JobType = TPayload.JobType,
            Payload = json,
            Priority = priority,
            VisibleAt = visibleAt ?? DateTime.UtcNow
        });

        _logger.LogDebug("Queued job {JobType} ({JobId})", TPayload.JobType, jobId);
    }

    /// <summary>Shared by the queue and the worker — they must agree on the wire format.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
