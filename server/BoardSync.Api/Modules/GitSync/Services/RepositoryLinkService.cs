using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.GitSync.Services;

/// <summary>Wires a repository to a project, and grants the installation what it needs to act.</summary>
public interface IRepositoryLinkService
{
    /// <summary>
    /// Links a repository to a project and gives the installation its project-scope grant.
    /// </summary>
    Task<RepositoryLink> LinkAsync(
        Guid installationId,
        Guid projectId,
        string repositoryExternalId,
        string repositoryName,
        string defaultBranch,
        Guid linkedBy,
        CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Linking a repository is what turns an installation into a principal on a project.</b> The
/// grant is the mechanism the whole QA gate rests on: the installation gets
/// <see cref="RoleType.Integration"/>, which permits contribution and carries neither
/// <c>workitem:verify</c> nor anything administrative — so automation can carry work as far as
/// "merged, awaiting test" and structurally cannot close it.
/// </para>
/// <para>
/// Granting it here rather than at installation time keeps the blast radius right: an installation
/// can see every repository on the account, and it should hold nothing on a project until somebody
/// deliberately wires a repository to it.
/// </para>
/// </remarks>
public class RepositoryLinkService : IRepositoryLinkService
{
    private readonly IGitRepository _repository;
    private readonly IRbacService _rbac;
    private readonly ILogger<RepositoryLinkService> _logger;

    public RepositoryLinkService(
        IGitRepository repository,
        IRbacService rbac,
        ILogger<RepositoryLinkService> logger)
    {
        _repository = repository;
        _rbac = rbac;
        _logger = logger;
    }

    public async Task<RepositoryLink> LinkAsync(
        Guid installationId,
        Guid projectId,
        string repositoryExternalId,
        string repositoryName,
        string defaultBranch,
        Guid linkedBy,
        CancellationToken ct = default)
    {
        var link = new RepositoryLink
        {
            InstallationId = installationId,
            ProjectId = projectId,
            RepositoryExternalId = repositoryExternalId,
            RepositoryName = repositoryName,
            DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim(),
            CreatedBy = linkedBy
        };

        _repository.AddLink(link);
        await _repository.SaveChangesAsync(ct);

        // The installation, not the person who linked it, is the principal. AssignRoleAsync is
        // idempotent, so linking a second repository to the same project does not duplicate it.
        await _rbac.AssignRoleAsync(
            installationId,
            RoleType.Integration,
            RoleScope.Project,
            projectId,
            linkedBy,
            PrincipalType.Integration,
            ct);

        _logger.LogInformation(
            "Linked {Repository} to project {ProjectId}; installation {InstallationId} granted Integration.",
            repositoryName, projectId, installationId);

        return link;
    }
}
