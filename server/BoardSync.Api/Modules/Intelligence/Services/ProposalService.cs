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
