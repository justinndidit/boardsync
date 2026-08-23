using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.GitSync.Models;

/// <summary>The git hosts BoardSync can connect to.</summary>
/// <remarks>
/// Stored by name, so adding a provider is additive. The order here is the order they are being
/// built: GitHub first, then the two that matter most to the teams this is aimed at.
/// </remarks>
public enum GitProvider
{
    GitHub,
    GitLab,
    AzureDevOps,
    Bitbucket
}

/// <summary>
/// How a webhook delivery proved it came from the provider.
/// </summary>
/// <remarks>
/// <para>
/// Recorded on every delivery, because it is <b>not the same across providers</b> and the difference
/// is security-relevant. GitHub, GitLab (with a signing token) and Bitbucket sign the payload, so a
/// verified delivery proves both origin and that the body was not altered. Azure DevOps Service
/// Hooks cannot sign at all — they offer Basic auth and custom headers over HTTPS — so a verified
/// ADO delivery proves only that the caller knew a secret.
/// </para>
/// <para>
/// Keeping it per row rather than inferring it from the provider means the audit answers "what was
/// this trusted on?" directly, and survives a provider gaining a stronger mechanism later.
/// </para>
/// </remarks>
public enum WebhookVerification
{
    /// <summary>HMAC over the raw body. Proves origin and integrity.</summary>
    HmacSha256,

    /// <summary>A shared secret presented verbatim. Proves the caller knew it; proves nothing about the body.</summary>
    SharedSecret,

    /// <summary>HTTP Basic credentials. As above, and the best Azure DevOps offers.</summary>
    BasicAuth
}

/// <summary>
/// One organization's connection to a git host.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to an organization rather than a project, because that is how the providers themselves
/// model it: a GitHub App is installed on an account and covers many repositories, and re-doing
/// that per project would make onboarding a repository a re-authorization.
/// </para>
/// <para>
/// <b>This is a principal, not just a configuration row.</b> When a repository is linked, the
/// installation is granted a project-scope role that permits contribution and deliberately not
/// certification — so the QA gate is a permission the integration lacks rather than a rule it is
/// trusted to follow. That grant lands with the binding work; see build_context.md §6.3.
/// </para>
/// </remarks>
public class GitProviderInstallation : BaseEntity
{
    public Guid OrganizationId { get; set; }

    public GitProvider Provider { get; set; }

    /// <summary>
    /// The provider's own id for this connection — a GitHub App installation id, a GitLab group id,
    /// an Azure DevOps organization name.
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Human label for the settings screen, e.g. the GitHub account name.</summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// The secret this installation's webhooks are verified with.
    /// </summary>
    /// <remarks>
    /// Per installation, never global: one customer's leaked secret must not let them forge another
    /// customer's deliveries. Encryption at rest is a deployment concern and a gap noted in the
    /// audit — this column holds it in the clear today.
    /// </remarks>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// The strongest verification this installation's provider offers.
    /// </summary>
    /// <remarks>
    /// Denormalized from the provider so the settings screen can show it, and so a provider gaining
    /// signing does not silently reclassify deliveries already recorded.
    /// </remarks>
    public WebhookVerification Verification { get; set; }

    /// <summary>
    /// A high-entropy path segment this installation's webhook URL carries.
    /// </summary>
    /// <remarks>
    /// Compensating control for the providers that cannot sign. For Azure DevOps the URL itself is
    /// most of the secret, so it is generated rather than derived and compared in constant time like
    /// any other credential. Harmless for the providers that do sign.
    /// </remarks>
    public string EndpointToken { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A repository wired to a project: the edge that decides which board a commit can move.
/// </summary>
/// <remarks>
/// <para>
/// Many-to-many in shape even though the API will accept only one project per repository at first.
/// Monorepos will want several, and modelling it now makes that a validation change rather than a
/// migration — the cost of the extra row today is nothing.
/// </para>
/// <para>
/// <b>The security boundary for binding.</b> A commit may only move work items in a project this
/// repository is linked to; a reference to anything else is ignored, whatever the message says.
/// </para>
/// </remarks>
public class RepositoryLink : BaseEntity
{
    public Guid InstallationId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The provider's stable id for the repository. Survives a rename; the name does not.</summary>
    public string RepositoryExternalId { get; set; } = string.Empty;

    /// <summary>Display name, e.g. <c>acme/payments</c>. Refreshed opportunistically.</summary>
    public string RepositoryName { get; set; } = string.Empty;

    /// <summary>
    /// The branch a merge has to land on to mean "done".
    /// </summary>
    /// <remarks>
    /// Stored rather than assumed: <c>main</c> is not universal, and a merge into a feature branch
    /// must not resolve a work item.
    /// </remarks>
    public string DefaultBranch { get; set; } = "main";

    public bool IsActive { get; set; } = true;

    public virtual GitProviderInstallation Installation { get; set; } = null!;
}

/// <summary>
/// One webhook delivery, recorded before anything is done with it.
/// </summary>
/// <remarks>
/// <para>
/// Written synchronously on the request, then processed by a job. That split is what keeps ingest
/// fast enough that a provider never times out on us — a 300-commit force-push must not make the
/// POST slow — and it is what makes the raw payload available to replay when a binding rule turns
/// out to be wrong.
/// </para>
/// <para>
/// <b><see cref="ProviderDeliveryId"/> is the idempotency key.</b> Providers deliver at least once
/// and redeliver on request, and GitHub reuses the original GUID when it does — so deduplicating on
/// it catches redeliveries, which is what you want.
/// </para>
/// </remarks>
public class WebhookDelivery : BaseEntity
{
    public Guid InstallationId { get; set; }

    public GitProvider Provider { get; set; }

    /// <summary>The provider's delivery id. Unique per provider.</summary>
    public string ProviderDeliveryId { get; set; } = string.Empty;

    /// <summary>The provider's name for the event, e.g. <c>push</c> or <c>pull_request</c>.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>The exact bytes received, as text.</summary>
    /// <remarks>
    /// Kept for a bounded window so a binding bug can be fixed and replayed without asking anyone to
    /// re-push. It is customer source metadata — branch names, commit messages, author emails — so
    /// the window is short and deliberate rather than "forever because storage is cheap".
    /// </remarks>
    public string Payload { get; set; } = string.Empty;

    /// <summary>What this delivery was trusted on. See <see cref="WebhookVerification"/>.</summary>
    public WebhookVerification Verification { get; set; }

    /// <summary>When processing finished. Null while queued.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Why this delivery did nothing, when it did nothing.
    /// </summary>
    /// <remarks>
    /// An event type nobody handles and an event that matched no work item are both normal and both
    /// invisible without this. It is the difference between "the integration is quiet" and "the
    /// integration is broken", which is otherwise a question nobody can answer.
    /// </remarks>
    public string? Outcome { get; set; }

    public virtual GitProviderInstallation Installation { get; set; } = null!;
}
