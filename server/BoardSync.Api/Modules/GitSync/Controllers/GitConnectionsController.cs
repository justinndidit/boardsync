using BoardSync.Api.Modules.GitSync.DTOs;
using BoardSync.Api.Modules.GitSync.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.GitSync.Controllers;

/// <summary>
/// Connecting a git host, and wiring its repositories to projects.
/// </summary>
/// <remarks>
/// <para>
/// Two different authorities, deliberately. A <b>connection</b> is an organization-wide credential
/// that reaches every repository on the account, so it needs <c>org:admin</c>. A <b>repository
/// link</b> decides which board a commit can move, so it needs <c>project:admin</c> on the project
/// receiving it — a project administrator can wire up their own project without being handed
/// authority over the organization's git account.
/// </para>
/// <para>
/// Linking additionally checks that the installation belongs to the project's own organization.
/// Without that, a project administrator in one organization could wire another organization's
/// repository to their project and receive its commit messages and branch names in the delivery
/// history.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Produces("application/json")]
public class GitConnectionsController : ControllerBase
{
    private readonly IGitConnectionService _connections;
    private readonly ICurrentUserContext _currentUser;

    public GitConnectionsController(IGitConnectionService connections, ICurrentUserContext currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The public origin this API is reached at, for building the webhook URL an admin pastes into
    /// the provider.
    /// </summary>
    /// <remarks>
    /// Taken from the request rather than from configuration, so a deployment behind a proxy gets the
    /// address callers actually use. <c>ForwardedHeaders</c> is what makes that trustworthy, and it is
    /// only honoured from proxies explicitly trusted in configuration — see <c>Program.cs</c>.
    /// </remarks>
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    // ── Installations ─────────────────────────────────────────────────────────

    /// <summary>Git hosts connected to this organization. Requires <c>org:admin</c>.</summary>
    [HttpGet("api/orgs/{orgId:guid}/git/installations")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<InstallationResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInstallations(Guid orgId, CancellationToken ct)
    {
        var installations = await _connections.GetInstallationsAsync(orgId, ct);

        return Ok(new ApiResponse<IReadOnlyList<InstallationResponse>>(
            true, "Installations retrieved.", installations));
    }

    /// <summary>
    /// Connect a git host to this organization. Requires <c>org:admin</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The response carries the webhook secret, and it is the only time it is shown.</b> It is
    /// not stored in a retrievable form on purpose: a credential that can be read back turns any
    /// future read-access bug into a credential leak. Lose it and rotate rather than recover it.
    /// </remarks>
    [HttpPost("api/orgs/{orgId:guid}/git/installations")]
    [RequirePermission(Permissions.OrgAdmin, From = "orgId")]
    [ProducesResponseType(typeof(ApiResponse<InstallationSecretsResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Connect(
        Guid orgId, [FromBody] ConnectInstallationRequest request, CancellationToken ct)
    {
        var (installation, secrets) = await _connections.ConnectAsync(
            orgId, request, _currentUser.UserId, BaseUrl, ct);

        return CreatedAtAction(
            nameof(GetInstallations), new { orgId },
            new ApiResponse<InstallationSecretsResponse>(
                true,
                "Connected. Store the webhook secret now — it cannot be shown again.",
                secrets));
    }

    /// <summary>
    /// Issue a new webhook secret. Requires <c>org:admin</c> on the installation's organization.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliveries signed with the previous secret are rejected from the moment this returns, so
    /// the provider's configuration has to be updated before the next push. The response repeats the
    /// URL alongside the new secret for exactly that reason.
    /// </remarks>
    [HttpPost("api/git/installations/{installationId:guid}/rotate-secret")]
    [RequirePermission(Permissions.OrgAdmin, From = "installationId")]
    [ProducesResponseType(typeof(ApiResponse<InstallationSecretsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateSecret(Guid installationId, CancellationToken ct)
    {
        var secrets = await _connections.RotateSecretAsync(installationId, BaseUrl, ct);

        return Ok(new ApiResponse<InstallationSecretsResponse>(
            true,
            "Secret rotated. Update the provider's webhook configuration before the next push.",
            secrets));
    }

    /// <summary>
    /// Disconnect a git host. Requires <c>org:admin</c> on the installation's organization.
    /// </summary>
    /// <remarks>
    /// Deactivates rather than deletes, so the delivery history — the only record of what the
    /// integration did to the board — survives. Every repository link is deactivated with it and the
    /// installation's project grants are revoked, which is what actually stops it acting.
    /// </remarks>
    [HttpDelete("api/git/installations/{installationId:guid}")]
    [RequirePermission(Permissions.OrgAdmin, From = "installationId")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(Guid installationId, CancellationToken ct)
    {
        await _connections.DisconnectAsync(installationId, ct);
        return NoContent();
    }

    /// <summary>
    /// Recent webhook deliveries and what each one did. Requires <c>org:admin</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to "is the integration working?", which is otherwise unanswerable: a quiet
    /// integration and a broken one look identical from the board. Each delivery's <c>outcome</c>
    /// says what it amounted to, including the cases where it deliberately did nothing — an event
    /// nobody handles, a repository nobody linked, a branch naming no work item.
    /// </para>
    /// <para>
    /// <c>org:admin</c> rather than anything project-scoped, because a delivery is not project-scoped
    /// — one covers every repository on the account, and its outcomes name branches across all of
    /// them.
    /// </para>
    /// </remarks>
    [HttpGet("api/git/installations/{installationId:guid}/deliveries")]
    [RequirePermission(Permissions.OrgAdmin, From = "installationId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DeliveryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeliveries(
        Guid installationId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var deliveries = await _connections.GetDeliveriesAsync(installationId, pagination, ct);

        return Ok(new ApiResponse<PagedResult<DeliveryResponse>>(
            true, "Deliveries retrieved.", deliveries));
    }

    // ── Repository links ──────────────────────────────────────────────────────

    /// <summary>Repositories feeding this project. Requires <c>project:read</c>.</summary>
    /// <remarks>
    /// Readable by anyone who can see the project: knowing which repository moves your board is part
    /// of understanding the board, not an administrative detail.
    /// </remarks>
    [HttpGet("api/projects/{projectId:guid}/git/repositories")]
    [RequirePermission(Permissions.ProjectRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RepositoryLinkResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLinks(Guid projectId, CancellationToken ct)
    {
        var links = await _connections.GetLinksAsync(projectId, ct);

        return Ok(new ApiResponse<IReadOnlyList<RepositoryLinkResponse>>(
            true, "Linked repositories retrieved.", links));
    }

    /// <summary>
    /// Wire a repository to this project. Requires <c>project:admin</c>.
    /// </summary>
    /// <remarks>
    /// This is what lets git move the project's board, and it is what grants the installation its
    /// project-scope role — one that permits contribution and deliberately not certification, so
    /// automation can carry work as far as "merged, awaiting test" and structurally cannot close it.
    /// </remarks>
    [HttpPost("api/projects/{projectId:guid}/git/repositories")]
    [RequirePermission(Permissions.ProjectAdmin, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<RepositoryLinkResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Link(
        Guid projectId, [FromBody] LinkRepositoryRequest request, CancellationToken ct)
    {
        var link = await _connections.LinkAsync(projectId, request, _currentUser.UserId, ct);

        return CreatedAtAction(
            nameof(GetLinks), new { projectId },
            new ApiResponse<RepositoryLinkResponse>(true, "Repository linked.", link));
    }

    /// <summary>
    /// Stop a repository feeding this project. Requires <c>project:admin</c>.
    /// </summary>
    /// <remarks>
    /// The installation's grant on the project is revoked only when this was its last repository
    /// here — two repositories feeding one project is the monorepo case, and unlinking one must not
    /// stop the other working.
    /// </remarks>
    [HttpDelete("api/projects/{projectId:guid}/git/repositories/{linkId:guid}")]
    [RequirePermission(Permissions.ProjectAdmin, From = "projectId")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlink(Guid projectId, Guid linkId, CancellationToken ct)
    {
        await _connections.UnlinkAsync(projectId, linkId, ct);
        return NoContent();
    }
}
