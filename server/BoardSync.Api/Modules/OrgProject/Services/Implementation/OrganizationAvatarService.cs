using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using BoardSync.Api.Shared.Storage;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

/// <inheritdoc cref="IOrganizationAvatarService"/>
/// <remarks>
/// The mirror of the profile-picture service, over a different record and a different notion of
/// who is allowed. The upload itself — content types, size ceiling, reading the bytes back to see
/// what really landed — is <see cref="AvatarUploadPipeline"/>, so there is one copy of the check
/// that keeps arbitrary files out of the container.
/// </remarks>
public class OrganizationAvatarService : IOrganizationAvatarService
{
    private readonly IOrganizationRepository _organizations;
    private readonly IOrganizationService _organizationService;
    private readonly AvatarUploadPipeline _pipeline;
    private readonly IEventBus _eventBus;
    private readonly ILogger<OrganizationAvatarService> _logger;

    public OrganizationAvatarService(
        IOrganizationRepository organizations,
        IOrganizationService organizationService,
        AvatarUploadPipeline pipeline,
        IEventBus eventBus,
        ILogger<OrganizationAvatarService> logger)
    {
        _organizations = organizations;
        _organizationService = organizationService;
        _pipeline = pipeline;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// What the activity feed calls this field.
    /// </summary>
    /// <remarks>
    /// "Logo" rather than "Avatar", matching what the settings screen labels it. Rows written
    /// before uploads existed say "Avatar"; nothing reads this back, so the two spellings simply
    /// coexist in the history.
    /// </remarks>
    private const string LogoField = "Logo";

    public bool IsAvailable => _pipeline.IsAvailable;

    public Task<ApiResponse<AvatarUploadTicketResponse>> CreateUploadTicketAsync(
        Guid orgId, AvatarUploadTicketRequest request, CancellationToken ct) =>
        _pipeline.CreateTicketAsync(AvatarOwner.ForOrganization(orgId), request, ct);

    public async Task<ApiResponse<OrganizationResponse>> CommitAsync(
        Guid orgId, SetProfilePictureRequest request, Guid actingUserId, CancellationToken ct)
    {
        var org = await _organizations.GetActiveAsync(orgId, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        var accepted = await _pipeline.AcceptAsync(
            AvatarOwner.ForOrganization(orgId), request.BlobPath, ct);

        if (!accepted.Accepted)
            return new ApiResponse<OrganizationResponse>(false, accepted.Error!);

        var previous = org.AvatarUrl;

        org.AvatarUrl = accepted.Url;
        org.UpdatedAt = DateTime.UtcNow;

        /*
         * "updated", not the URL.
         *
         * The activity feed renders old and new as `Field: before → after`, so recording the URLs
         * put 280 characters of two near-identical GUID paths under the entry — and the "before"
         * half is a dead link by the time anyone reads it, because the blob it names is deleted a
         * few lines below. Neither value is showable, so the slot carries what actually happened.
         *
         * Nothing else is lost: an avatar has no readable value to report the way a name or a
         * description does, and the picture itself is on the organization for anyone who wants to
         * see it. This follows what the membership events already do — they resolve a user id to a
         * name rather than logging the id.
         */
        _eventBus.Enqueue(
            new OrganizationUpdated(
                org.Id, org.Name, LogoField, null, "updated", actingUserId));

        await _organizations.SaveChangesAsync(ct);

        // Only after the record is committed — see AvatarUploadPipeline.DeleteIfOursAsync.
        if (previous is not null && previous != org.AvatarUrl)
            await _pipeline.DeleteIfOursAsync(previous, ct);

        _logger.LogInformation(
            "Organization logo updated for {OrgId} by {UserId}", orgId, actingUserId);

        return new ApiResponse<OrganizationResponse>(
            true,
            "Organization logo updated",
            await _organizationService.GetByIdAsync(orgId, actingUserId, ct));
    }

    public async Task<ApiResponse<OrganizationResponse>> RemoveAsync(
        Guid orgId, Guid actingUserId, CancellationToken ct)
    {
        var org = await _organizations.GetActiveAsync(orgId, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        var previous = org.AvatarUrl;

        org.AvatarUrl = null;
        org.UpdatedAt = DateTime.UtcNow;

        if (previous is not null)
        {
            // Same slot, same reason. Passing the old URL as "before" would render
            // "Logo: http://… removed", which names a blob that no longer exists.
            _eventBus.Enqueue(
                new OrganizationUpdated(
                    org.Id, org.Name, LogoField, null, "removed", actingUserId));
        }

        await _organizations.SaveChangesAsync(ct);

        await _pipeline.DeleteIfOursAsync(previous, ct);

        return new ApiResponse<OrganizationResponse>(
            true,
            "Organization logo removed",
            await _organizationService.GetByIdAsync(orgId, actingUserId, ct));
    }
}
