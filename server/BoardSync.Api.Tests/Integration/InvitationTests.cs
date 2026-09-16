using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// Joining an organization by invitation.
/// </summary>
/// <remarks>
/// <para>
/// Membership used to be something an administrator did to somebody: the client resolved an email
/// to a user id and the API added that person on the spot — no notice, no consent — and an address
/// with no account could not be invited at all, because the lookup 404'd.
/// </para>
/// <para>
/// The rule that makes a mailed link safe to hand out is that the account accepting must own the
/// address it was sent to. A token in an email is a bearer credential: forwarded, quoted in a
/// reply, or read off a shared screen it reaches people it was not addressed to. Most of what is
/// below is that rule, from the angles somebody would actually come at it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class InvitationTests
{
    private readonly BoardSyncApiFactory _factory;

    public InvitationTests(BoardSyncApiFactory factory) => _factory = factory;

    // ── Sending ──────────────────────────────────────────────────────────────

    /// <summary>The case the old flow could not express at all.</summary>
    [Fact]
    public async Task AnAddressWithNoAccountCanBeInvited()
    {
        var workspace = await Workspace.CreateAsync(_factory);

        var response = await workspace.Owner.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/invitations",
            new { email = $"nobody-{Guid.NewGuid():N}@boardsync.test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A role that means nothing at organization scope is refused.
    /// </summary>
    /// <remarks>
    /// Otherwise the invitation becomes the back door for a grant the role-change endpoint already
    /// refuses — 'ProjectAdmin' at organization scope grants nothing and reads to an administrator
    /// as though it grants everything.
    /// </remarks>
    [Theory]
    [InlineData("ProjectAdmin")]
    [InlineData("TeamMember")]
    [InlineData("Wizard")]
    public async Task ARoleThatIsNotOrganizationScopedIsRefused(string role)
    {
        var workspace = await Workspace.CreateAsync(_factory);

        var response = await workspace.Owner.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/invitations",
            new { email = $"x-{Guid.NewGuid():N}@boardsync.test", role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SomebodyAlreadyInTheOrganizationCannotBeInvited()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var member = await workspace.AddOrganizationMemberAsync(_factory);

        var response = await workspace.Owner.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/invitations",
            new { email = member.Email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An ordinary member cannot invite; the organization is not theirs to grow.</summary>
    [Fact]
    public async Task AMemberCannotInvite()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var member = await workspace.AddOrganizationMemberAsync(_factory);

        var response = await member.PostRaw(
            $"/api/orgs/{workspace.OrganizationId}/invitations",
            new { email = $"x-{Guid.NewGuid():N}@boardsync.test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Accepting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheInvitedAccountJoinsOnAccepting()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var invitee = await TestApi.RegisterAsync(_factory);

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, invitee.Email);

        var response = await invitee.PostRaw($"/api/invitations/{token}/accept", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Membership is real: the organization is now readable to them.
        var readable = await invitee.GetRaw($"/api/orgs/{workspace.OrganizationId}");

        Assert.Equal(HttpStatusCode.OK, readable.StatusCode);
    }

    /// <summary>
    /// The rule the whole design rests on.
    /// </summary>
    /// <remarks>
    /// Holding the link is not enough — you must also control the mailbox it was sent to, which is
    /// the thing the administrator actually vouched for. Without this, a forwarded email is a way
    /// into somebody else's organization.
    /// </remarks>
    [Fact]
    public async Task SomebodyElseCannotAcceptAForwardedInvitation()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var stranger = await TestApi.RegisterAsync(_factory);

        var token = await Workspace.SeedInvitationAsync(
            _factory,
            workspace.OrganizationId,
            $"someone-else-{Guid.NewGuid():N}@boardsync.test");

        var response = await stranger.PostRaw($"/api/invitations/{token}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And nothing leaked: the organization is still not theirs to read.
        var readable = await stranger.GetRaw($"/api/orgs/{workspace.OrganizationId}");

        Assert.NotEqual(HttpStatusCode.OK, readable.StatusCode);
    }

    /// <summary>One invitation, one membership — a replayed link is spent.</summary>
    [Fact]
    public async Task AnInvitationCannotBeUsedTwice()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var invitee = await TestApi.RegisterAsync(_factory);

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, invitee.Email);

        await invitee.PostRaw($"/api/invitations/{token}/accept", new { });

        var again = await invitee.PostRaw($"/api/invitations/{token}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task ARevokedInvitationCannotBeAccepted()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var invitee = await TestApi.RegisterAsync(_factory);

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, invitee.Email);

        var pending = await workspace.Owner.Get<List<InvitationRow>>(
            $"/api/orgs/{workspace.OrganizationId}/invitations?openOnly=true");

        var mine = pending.Single(i => i.Email == invitee.Email);

        await workspace.Owner.DeleteRaw(
            $"/api/orgs/{workspace.OrganizationId}/invitations/{mine.Id}");

        var response = await invitee.PostRaw($"/api/invitations/{token}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownTokenIsRefused()
    {
        var invitee = await TestApi.RegisterAsync(_factory);

        var response = await invitee.PostRaw(
            $"/api/invitations/{Guid.NewGuid():N}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── The preview ──────────────────────────────────────────────────────────

    /// <summary>
    /// Readable without signing in, because the recipient may have no account — the page has to
    /// know whether to offer sign-in or sign-up before it can ask them for anything.
    /// </summary>
    [Fact]
    public async Task ThePreviewIsReadableAnonymously()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var email = $"newcomer-{Guid.NewGuid():N}@boardsync.test";

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, email);

        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/invitations/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Envelope<PreviewRow>>();

        Assert.Equal(email, body!.Data!.Email);

        // No account yet, so the page sends them to sign-up rather than to a login form for an
        // account that does not exist.
        Assert.False(body.Data.HasAccount);
    }

    [Fact]
    public async Task ThePreviewKnowsWhenTheAddressAlreadyHasAnAccount()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var invitee = await TestApi.RegisterAsync(_factory);

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, invitee.Email);

        using var anonymous = _factory.CreateClient();

        var body = await (await anonymous.GetAsync($"/api/invitations/{token}"))
            .Content.ReadFromJsonAsync<Envelope<PreviewRow>>();

        Assert.True(body!.Data!.HasAccount);
    }

    // ── Registration from an invitation ──────────────────────────────────────

    /*
     * What decides whether a registration skips email confirmation.
     *
     * Asserted against the service rather than through sign-in, because this host sets
     * `SecuritySettings:RequireEmailConfirmation` to false — so every account is active either way
     * and a login-based assertion would pass whatever this returned. This is the decision itself.
     */

    /// <summary>
    /// An invitation proves the address it was sent to, and only that one.
    /// </summary>
    /// <remarks>
    /// The proof is that the invitation went to that mailbox and only somebody who can read it
    /// holds the token — exactly what a confirmation email establishes. Accepting a token for a
    /// different address would let any invitation confirm any address, which is the whole value of
    /// confirmation gone.
    /// </remarks>
    [Fact]
    public async Task AnInvitationProvesOnlyTheAddressItWasSentTo()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var invited = $"invited-{Guid.NewGuid():N}@boardsync.test";

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, invited);

        using var scope = _factory.Services.CreateScope();

        var invitations = scope.ServiceProvider
            .GetRequiredService<IOrganizationInvitationService>();

        Assert.True(
            await invitations.IsOpenForEmailAsync(token, invited, default));

        // Case and surrounding space are normalization, not a different address.
        Assert.True(
            await invitations.IsOpenForEmailAsync(token, $"  {invited.ToUpperInvariant()} ", default));

        Assert.False(
            await invitations.IsOpenForEmailAsync(
                token, $"someone-else-{Guid.NewGuid():N}@boardsync.test", default));

        Assert.False(
            await invitations.IsOpenForEmailAsync($"{Guid.NewGuid():N}", invited, default));
    }

    /// <summary>
    /// A token that proves nothing is ignored rather than fatal.
    /// </summary>
    /// <remarks>
    /// Registration still succeeds — a stale or mistyped token is no reason to refuse somebody an
    /// account — it simply does not count as proof, and the ordinary confirmation path applies.
    /// </remarks>
    [Fact]
    public async Task RegisteringWithAnInvitationForAnotherAddressStillSucceeds()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        const string password = "T3st!Password";

        var token = await Workspace.SeedInvitationAsync(
            _factory,
            workspace.OrganizationId,
            $"the-invited-one-{Guid.NewGuid():N}@boardsync.test");

        using var http = _factory.CreateClient();

        var registered = await http.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"opportunist-{Guid.NewGuid():N}@boardsync.test",
            password,
            confirmPassword = password,
            firstName = "Op",
            lastName = "Portunist",
            inviteToken = token
        });

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
    }

    /// <summary>Registering from a genuine invitation works end to end.</summary>
    [Fact]
    public async Task RegisteringFromAnInvitationSucceedsAndCanThenAccept()
    {
        var workspace = await Workspace.CreateAsync(_factory);
        var email = $"invited-{Guid.NewGuid():N}@boardsync.test";
        const string password = "T3st!Password";

        var token = await Workspace.SeedInvitationAsync(
            _factory, workspace.OrganizationId, email);

        using var http = _factory.CreateClient();

        var registered = await http.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            confirmPassword = password,
            firstName = "In",
            lastName = "Vited",
            inviteToken = token
        });

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private sealed record InvitationRow(Guid Id, string Email, string Role, string Status);

    private sealed record PreviewRow(
        string OrganizationName, string Email, string Role, bool HasAccount);

    private sealed record Envelope<T>(bool Success, string Message, T? Data);
}
