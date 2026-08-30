using BoardSync.Api.Shared.Kernel;
using System.Text.Json;

using BoardSync.Api.Data;
using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Jobs;
using BoardSync.Api.Modules.Intelligence.Models;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Services;
using BoardSync.Api.Shared.Kernel.Exceptions;
using BoardSync.Api.Shared.Kernel.Jobs;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>Creates proposals, and turns accepted ones into real work items.</summary>
public interface IProposalService
{
    Task<Guid> RequestAsync(
        Guid projectId, DecomposeRequest request, Guid requestedBy, CancellationToken ct = default);

    Task<ProposalView> GetAsync(Guid proposalId, CancellationToken ct = default);

    /// <summary>Every proposal a project has produced, newest first.</summary>
    Task<PagedResult<ProposalSummary>> ListAsync(
        Guid projectId, PaginationQuery pagination, CancellationToken ct = default);

    Task<AcceptanceResult> AcceptAsync(
        Guid proposalId, AcceptProposalRequest request, Guid acceptedBy, CancellationToken ct = default);

    Task RejectAsync(Guid proposalId, Guid rejectedBy, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Acceptance is the only thing here that writes to the board</b>, and it writes through
/// <see cref="IWorkItemService.CreateAsync"/> — the same call a person clicking "New work item"
/// makes, with the same validation, the same events, and the same history rows. Nothing in this
/// module has a privileged path to the domain, which is what build_context.md §8.1 means by the
/// proposal having no authority.
/// </para>
/// </remarks>
public sealed class ProposalService : IProposalService
{
    private readonly BoardSyncDbContext _context;
    private readonly IWorkItemService _workItems;
    private readonly IJobQueue _jobs;
    private readonly ILogger<ProposalService> _logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ProposalService(
        BoardSyncDbContext context,
        IWorkItemService workItems,
        IJobQueue jobs,
        ILogger<ProposalService> logger)
    {
        _context = context;
        _workItems = workItems;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<Guid> RequestAsync(
        Guid projectId,
        DecomposeRequest request,
        Guid requestedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new BusinessRuleException("There is no document to decompose.");

        var project = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.OrganizationId, p.AssignedTeamId })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Project", projectId);

        /*
         * The team is the project's, unless one was named.
         *
         * Either way it is resolved now rather than at acceptance: it decides who can be assigned
         * the resulting work, and a proposal that cannot say who could own its output is one nobody
         * can act on.
         */
        var teamId = request.TeamId != Guid.Empty
            ? request.TeamId
            : project.AssignedTeamId;

        if (teamId == Guid.Empty)
        {
            throw new BusinessRuleException(
                "This project has no assigned team, so there is nobody to assign the work to. " +
                "Name a team on the request or assign one to the project.");
        }

        var proposal = new Proposal
        {
            ProjectId = projectId,
            OrganizationId = project.OrganizationId,
            TeamId = teamId,
            Status = ProposalStatus.Pending,
            SourceText = request.Content,
            CreatedBy = requestedBy,
        };

        _context.Proposals.Add(proposal);

        /*
         * Queued before the save, not after — the job row and the proposal row commit together.
         *
         * Enqueueing afterwards leaves the job in the change tracker with nothing left to persist
         * it, which is how every work item domain event went missing for months (audit finding 15).
         * The proposal id doubles as the job's idempotency key: one proposal, one decomposition.
         */
        _jobs.Enqueue(proposal.Id, new DecomposePrd(proposal.Id), JobPriority.Normal);

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Proposal {ProposalId} requested for project {ProjectId} by {UserId}",
            proposal.Id, projectId, requestedBy);

        return proposal.Id;
    }

    public async Task<ProposalView> GetAsync(Guid proposalId, CancellationToken ct = default)
    {
        var proposal = await _context.Proposals
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new NotFoundException("Proposal", proposalId);

        return View(proposal);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Proposals are kept after the decision, and this is what makes that worth anything: without a
    /// listing, a proposal was reachable only by its id, which nothing recorded — so the record that
    /// <c>docs/adr-002-proposals.md</c> keeps deliberately could not be read back.
    /// </para>
    /// <para>
    /// Newest first, and the draft is left out. A page of thirty proposals would otherwise carry
    /// thirty hierarchies nobody asked to read.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<ProposalSummary>> ListAsync(
        Guid projectId, PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _context.Proposals
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id);

        var total = await query.CountAsync(ct);

        var page = await query
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

        return new PagedResult<ProposalSummary>(
            [.. page.Select(Summarize)],
            total,
            pagination.Page,
            pagination.PageSize);
    }

    public async Task<AcceptanceResult> AcceptAsync(
        Guid proposalId,
        AcceptProposalRequest request,
        Guid acceptedBy,
        CancellationToken ct = default)
    {
        var proposal = await _context.Proposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new NotFoundException("Proposal", proposalId);

        /*
         * Only a Ready proposal can be accepted, and accepting is one-way.
         *
         * Without this, a double-submitted form creates the whole tree twice — and the second copy
         * is indistinguishable from the first on the board, so somebody has to work out by hand
         * which forty items to delete.
         */
        if (proposal.Status != ProposalStatus.Ready)
        {
            throw new BusinessRuleException(
                $"This proposal is {proposal.Status.ToString().ToLowerInvariant()} and cannot be accepted.");
        }

        var draft = JsonSerializer.Deserialize<Decomposition>(proposal.DraftJson ?? "", Json)
            ?? throw new BusinessRuleException("This proposal has no draft to accept.");

        var assignee = request.AssignTo ?? acceptedBy;

        var selected = ProposalSelection.Resolve(draft.Roots, request.Include);

        if (selected.Count == 0)
            throw new BusinessRuleException("No work items were selected.");

        /*
         * One transaction around the whole tree.
         *
         * CreateAsync saves per item, so without this a failure on the twentieth of forty leaves
         * nineteen real work items on the board and a proposal still marked Ready — the board gains
         * half a plan, and re-accepting would duplicate the half that worked.
         */
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        var created = new List<Guid>(selected.Count);

        // Node id → the work item it became, so a child can name its parent. Parents are always
        // created before their children, which the walk below guarantees.
        var realIds = new Dictionary<string, Guid>();

        try
        {
            foreach (var (node, parentNodeId) in selected)
            {
                var response = await _workItems.CreateAsync(
                    proposal.ProjectId,
                    new CreateWorkItemRequest
                    {
                        Title = node.Title,
                        Description = node.Description,
                        Type = node.Type.ToString(),
                        Priority = node.Priority,
                        AssigneeId = assignee,
                        TeamId = proposal.TeamId,
                        ParentId = parentNodeId is not null && realIds.TryGetValue(parentNodeId, out var parentId)
                            ? parentId
                            : null,
                        StoryPoints = node.StoryPoints,
                    },
                    acceptedBy,
                    ct);

                realIds[node.Id] = response.Id;
                created.Add(response.Id);
            }

            proposal.Status = ProposalStatus.Accepted;
            proposal.DecidedBy = acceptedBy;
            proposal.DecidedAt = DateTime.UtcNow;
            proposal.AcceptedCount = created.Count;
            proposal.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        _logger.LogInformation(
            "Proposal {ProposalId} accepted by {UserId}: {Count} work items created",
            proposalId, acceptedBy, created.Count);

        return new AcceptanceResult(proposalId, created.Count, created);
    }

    public async Task RejectAsync(Guid proposalId, Guid rejectedBy, CancellationToken ct = default)
    {
        var proposal = await _context.Proposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new NotFoundException("Proposal", proposalId);

        if (proposal.Status is ProposalStatus.Accepted)
            throw new BusinessRuleException("This proposal has already been accepted.");

        proposal.Status = ProposalStatus.Rejected;
        proposal.DecidedBy = rejectedBy;
        proposal.DecidedAt = DateTime.UtcNow;
        proposal.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>How long a preview of the source document to keep in a listing.</summary>
    /// <remarks>
    /// Enough to tell two proposals apart at a glance and short enough that a page of them is not a
    /// page of PRDs. The full text stays on the row for anyone who opens it.
    /// </remarks>
    private const int PreviewLength = 140;

    private static ProposalSummary Summarize(Proposal proposal)
    {
        /*
         * The node count is read from the stored draft rather than kept as a column. It is derived
         * data, and a column would be one more thing that can disagree with the JSON beside it.
         */
        int? nodes = null;

        if (proposal.DraftJson is not null)
        {
            try
            {
                var draft = JsonSerializer.Deserialize<Decomposition>(proposal.DraftJson, Json);

                nodes = draft is null ? null : Count(draft.Roots);
            }
            catch (JsonException)
            {
                // A draft that no longer parses is a row from an older shape. The proposal is still
                // worth listing — it has a status and a date — so the count is simply absent.
            }
        }

        var source = proposal.SourceText.Trim();

        return new ProposalSummary(
            proposal.Id,
            proposal.ProjectId,
            proposal.Status.ToString(),
            proposal.FailureReason,
            proposal.TokensSpent,
            proposal.AcceptedCount,
            nodes,
            source.Length <= PreviewLength ? source : source[..PreviewLength] + "…",
            proposal.CreatedAt,
            proposal.DecidedAt);
    }

    private static int Count(IReadOnlyList<ProposedNode> nodes) =>
        nodes.Sum(node => 1 + Count(node.Children));

    private static ProposalView View(Proposal proposal) => new(
        proposal.Id,
        proposal.ProjectId,
        proposal.Status.ToString(),
        proposal.DraftJson is null
            ? null
            : JsonSerializer.Deserialize<Decomposition>(proposal.DraftJson, Json),
        proposal.FailureReason,
        proposal.TokensSpent,
        proposal.AcceptedCount,
        proposal.CreatedAt);
}
