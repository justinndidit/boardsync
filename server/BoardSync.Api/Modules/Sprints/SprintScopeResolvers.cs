using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Shared.Auth.Authorization;

namespace BoardSync.Api.Modules.Sprints;

/// <summary>
/// Resolves a sprint to the team that runs it.
/// </summary>
/// <remarks>
/// Sprints are team-scoped — they have no scope of their own — so every sprint permission is really
/// a question about its team.
/// </remarks>
public sealed class SprintScopeResolver(ISprintRepository repository) : IScopeResolver
{
    public string RouteParameter => "sprintId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var sprint = await repository.GetByIdAsync(value, ct);

        return sprint is null ? null : new ResolvedScope(RoleScope.Team, sprint.ProjectId);
    }
}

/// <summary>
/// Resolves a board to its project.
/// </summary>
public sealed class BoardScopeResolver(IBoardRepository repository) : IScopeResolver
{
    public string RouteParameter => "boardId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var board = await repository.GetWithColumnsAsync(value, ct);

        return board is null ? null : new ResolvedScope(RoleScope.Project, board.ProjectId);
    }
}

/// <summary>
/// Resolves a board column to the project whose board it belongs to.
/// </summary>
public sealed class BoardColumnScopeResolver(IBoardRepository repository) : IScopeResolver
{
    public string RouteParameter => "columnId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var projectId = await repository.GetProjectIdForColumnAsync(value, ct);

        return projectId is Guid id ? new ResolvedScope(RoleScope.Project, id) : null;
    }
}
