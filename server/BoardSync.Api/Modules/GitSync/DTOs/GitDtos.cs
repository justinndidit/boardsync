using System.ComponentModel.DataAnnotations;
using BoardSync.Api.Modules.GitSync.Models;

namespace BoardSync.Api.Modules.GitSync.DTOs;

/// <summary>Connect an organization to a git host.</summary>
public class ConnectInstallationRequest
{
    /// <summary>Which host. <c>GitHub</c> is the only one implemented so far.</summary>
    [Required]
    public GitProvider Provider { get; init; }

    /// <summary>
    /// The provider's own id for this connection — a GitHub App installation id, an Azure DevOps
    /// organization name.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ExternalId { get; init; } = string.Empty;

    /// <summary>A label for the settings screen, e.g. the GitHub account name.</summary>
    [Required]
    [MaxLength(200)]
    public string AccountName { get; init; } = string.Empty;
}

/// <summary>
/// A connected installation, as it appears in settings.
/// </summary>
/// <remarks>
/// Carries no secret. See <see cref="InstallationSecretsResponse"/> for why the secret is shown
/// once and never again.
/// </remarks>
public record InstallationResponse(
    Guid Id,
    GitProvider Provider,
    string ExternalId,
    string AccountName,
    WebhookVerification Verification,
    bool IsActive,
    int LinkedRepositoryCount,
    DateTime CreatedAt);

/// <summary>
/// What has to be pasted into the provider's webhook configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Returned once, when the installation is created or its secret is rotated, and never
/// retrievable afterwards.</b> Storing a credential that can be read back turns every future
/// read-access bug into a credential leak; making it unrecoverable means the worst case is somebody
/// has to rotate it, which is a button.
/// </para>
/// <para>
/// The URL contains a high-entropy segment identifying the installation. For providers that cannot
/// sign payloads — Azure DevOps — that segment is most of the security, so it is as much a secret as
/// the signing key.
/// </para>
/// </remarks>
public record InstallationSecretsResponse(
    Guid Id,
    string WebhookUrl,
    string WebhookSecret,
    WebhookVerification Verification,
    string Guidance);

/// <summary>Wire a repository to a project.</summary>
public class LinkRepositoryRequest
{
    /// <summary>The installation the repository belongs to. Must be in this project's organization.</summary>
    [Required]
    public Guid InstallationId { get; init; }

    /// <summary>
    /// The provider's stable id for the repository. Survives a rename; the name does not, which is
    /// why this is what is stored.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string RepositoryExternalId { get; init; } = string.Empty;

    /// <summary>Display name, e.g. <c>acme/payments</c>.</summary>
    [Required]
    [MaxLength(400)]
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>
    /// The branch a merge has to land on to mean "done". Defaults to <c>main</c>.
    /// </summary>
    /// <remarks>
    /// Not assumed: a merge into a feature branch is ordinary work, and resolving a work item on one
    /// would mark things done that are nowhere near it.
    /// </remarks>
    [MaxLength(200)]
    public string? DefaultBranch { get; init; }
}

/// <summary>A repository wired to a project.</summary>
public record RepositoryLinkResponse(
    Guid Id,
    Guid InstallationId,
    GitProvider Provider,
    string RepositoryExternalId,
    string RepositoryName,
    string DefaultBranch,
    DateTime CreatedAt);

/// <summary>
/// One webhook delivery and what it amounted to.
/// </summary>
/// <remarks>
/// The answer to "is the integration working?", which is otherwise unanswerable — a quiet
/// integration and a broken one look identical from the board. <see cref="Outcome"/> says what each
/// delivery did, including when it deliberately did nothing.
/// </remarks>
public record DeliveryResponse(
    Guid Id,
    GitProvider Provider,
    string EventName,
    WebhookVerification Verification,
    DateTime ReceivedAt,
    DateTime? ProcessedAt,
    string? Outcome);
