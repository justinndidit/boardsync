using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Shared.Auth.Authorization;

namespace BoardSync.Api.Modules.WorkItems;

/// <summary>
/// Resolves work item ids, and the ids of things hanging off them, to the project that governs them.
/// </summary>
/// <remarks>
/// Work items, their comments and their links all take their permissions from the project the item
/// belongs to. These let an endpoint keyed on any of those be authorized before it loads anything.
/// </remarks>
public sealed class WorkItemScopeResolver(IWorkItemRepository repository) : IScopeResolver
{
    public string RouteParameter => "workItemId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var item = await repository.GetActiveAsync(value, ct);

        return item is null ? null : new ResolvedScope(RoleScope.Project, item.ProjectId);
    }
}

/// <inheritdoc cref="WorkItemScopeResolver"/>
public sealed class WorkItemCommentScopeResolver(IWorkItemRepository repository) : IScopeResolver
{
    public string RouteParameter => "commentId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var projectId = await repository.GetProjectIdForCommentAsync(value, ct);

        return projectId is Guid id ? new ResolvedScope(RoleScope.Project, id) : null;
    }
}

/// <inheritdoc cref="WorkItemScopeResolver"/>
public sealed class WorkItemLinkScopeResolver(IWorkItemRepository repository) : IScopeResolver
{
    public string RouteParameter => "linkId";

    public async Task<ResolvedScope?> ResolveAsync(Guid value, CancellationToken ct)
    {
        var projectId = await repository.GetProjectIdForLinkAsync(value, ct);

        return projectId is Guid id ? new ResolvedScope(RoleScope.Project, id) : null;
    }
}
