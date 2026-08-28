using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Authorization;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Intelligence;

/// <summary>
/// Resolves a proposal to the project its work would be created in.
/// </summary>
/// <remarks>
/// A proposal is a draft of work items for one project, so the authority to read or accept it is
/// the authority to create work there. Resolving to the project rather than the organization is the
/// tighter of the two answers and the correct one: an organization administrator with no standing
/// in this project has no business accepting a plan into it.
/// </remarks>
public sealed class ProposalScopeResolver(BoardSyncDbContext context) : IScopeResolver
{
    public string RouteParameter => "proposalId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var projectId = await context.Proposals
            .Where(p => p.Id == value)
            .Select(p => (Guid?)p.ProjectId)
            .FirstOrDefaultAsync(ct);

        return projectId is Guid id ? new ResolvedScope(RoleScope.Project, id) : null;
    }
}
