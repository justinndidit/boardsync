using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// One signed-in user's view of the API.
/// </summary>
/// <remarks>
/// Every response is wrapped in <c>ApiResponse</c>, every enum is a string and every property is
/// camelCase, so unwrapping that once here keeps the tests about behaviour rather than about
/// deserialization.
/// </remarks>
public sealed class TestApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public Guid UserId { get; private init; }
    public string Email { get; private init; } = "";

    private TestApi(HttpClient http) => _http = http;

    /// <summary>
    /// Registers a new user and returns a client authenticated as them.
    /// </summary>
    /// <remarks>
    /// A fresh identity per call, keyed on a GUID, because the fixture's database is shared across
    /// test classes and an email collision would surface as a confusing 409 in an unrelated test.
    /// </remarks>
    public static async Task<TestApi> RegisterAsync(BoardSyncApiFactory factory)
    {
        var http = factory.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@boardsync.test";
        const string password = "T3st!Password";

        var registered = await http.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            confirmPassword = password,
            firstName = "Test",
            lastName = "User",
            displayName = "Test User"
        });

        registered.EnsureSuccessStatusCode();

        var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();

        var token = (await Unwrap<LoginData>(login))!.AccessToken;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = await Unwrap<IdOnly>(await http.GetAsync("/api/users/me"));

        return new TestApi(http) { UserId = me!.Id, Email = email };
    }

    // ── Verbs ─────────────────────────────────────────────────────────────────

    /// <summary>Sends a GET and returns the unwrapped <c>data</c>, asserting success.</summary>
    public async Task<T> Get<T>(string url)
    {
        var response = await _http.GetAsync(url);
        await AssertSuccess(response, "GET", url);
        return (await Unwrap<T>(response))!;
    }

    /// <summary>Sends a POST and returns the unwrapped <c>data</c>, asserting success.</summary>
    public async Task<T> Post<T>(string url, object body)
    {
        var response = await _http.PostAsJsonAsync(url, body);
        await AssertSuccess(response, "POST", url);
        return (await Unwrap<T>(response))!;
    }

    /// <summary>Sends a POST, asserting success but discarding the body.</summary>
    public async Task Post(string url, object body) =>
        await AssertSuccess(await _http.PostAsJsonAsync(url, body), "POST", url);

    /// <summary>Sends a DELETE, asserting success but discarding the body.</summary>
    public async Task Delete(string url) =>
        await AssertSuccess(await _http.DeleteAsync(url), "DELETE", url);

    /// <summary>Sends a PUT, asserting success but discarding the body.</summary>
    public async Task Put(string url, object body) =>
        await AssertSuccess(await _http.PutAsJsonAsync(url, body), "PUT", url);

    /// <summary>Sends a PATCH and returns the unwrapped <c>data</c>, asserting success.</summary>
    public async Task<T> Patch<T>(string url, object body)
    {
        var response = await _http.PatchAsJsonAsync(url, body);
        await AssertSuccess(response, "PATCH", url);
        return (await Unwrap<T>(response))!;
    }

    // ── Raw forms, for when the failure is the thing under test ───────────────

    public Task<HttpResponseMessage> GetRaw(string url) => _http.GetAsync(url);

    public Task<HttpResponseMessage> DeleteRaw(string url) => _http.DeleteAsync(url);

    public Task<HttpResponseMessage> PostRaw(string url, object body) =>
        _http.PostAsJsonAsync(url, body);

    public Task<HttpResponseMessage> PatchRaw(string url, object body) =>
        _http.PatchAsJsonAsync(url, body);

    public Task<HttpResponseMessage> PutRaw(string url, object body) =>
        _http.PutAsJsonAsync(url, body);

    /// <summary>Sends an arbitrary request, for when the HTTP method itself is the thing under test.</summary>
    public Task<HttpResponseMessage> SendRaw(HttpRequestMessage request) => _http.SendAsync(request);

    /// <summary>Sends a GET carrying an <c>If-None-Match</c>, for cache revalidation tests.</summary>
    public Task<HttpResponseMessage> GetRawWithETag(string url, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return _http.SendAsync(request);
    }

    /// <summary>The <c>message</c> from a wrapped response — what a rejection actually said.</summary>
    public static async Task<string> MessageOf(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<object>>(Json);
        return envelope?.Message ?? "";
    }

    private static async Task<T?> Unwrap<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope<T>>(Json))!.Data;

    /// <summary>
    /// Fails with the server's own message rather than a bare status code.
    /// </summary>
    /// <remarks>
    /// A 422 whose body explains which rule was broken is far more useful in a test failure than
    /// "expected success, got UnprocessableEntity", and these endpoints always say why.
    /// </remarks>
    private static async Task AssertSuccess(HttpResponseMessage response, string verb, string url)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail($"{verb} {url} returned {(int)response.StatusCode} {response.StatusCode}.\n{body}");
    }

    private sealed record Envelope<T>(bool Success, string Message, T? Data);
    private sealed record LoginData(string AccessToken);
    private sealed record IdOnly(Guid Id);
}

/// <summary>An organization with a team and a project, owned by the user who built it.</summary>
/// <param name="Owner">The creator, who is the organization's OrgAdmin and a member of its team.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="TeamId">The team the project is assigned to; <paramref name="Owner"/> is a member.</param>
/// <param name="ProjectId">The project.</param>
public sealed record Workspace(TestApi Owner, Guid OrganizationId, Guid TeamId, Guid ProjectId)
{
    /// <summary>
    /// Builds the smallest structure that lets work items exist: an org, a team the creator belongs
    /// to, and a project assigned to that team.
    /// </summary>
    /// <remarks>
    /// The creator joins the team because a work item must be assigned to someone on the owning
    /// team, so without it every test would have to do this dance itself.
    /// </remarks>
    public static async Task<Workspace> CreateAsync(BoardSyncApiFactory factory)
    {
        var owner = await TestApi.RegisterAsync(factory);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var org = await owner.Post<Created>("/api/orgs", new { name = $"Org {suffix}" });
        var team = await owner.Post<Created>($"/api/orgs/{org.Id}/teams", new { name = $"Team {suffix}" });

        await owner.Post($"/api/teams/{team.Id}/members", new { userId = owner.UserId });

        var project = await owner.Post<Created>($"/api/orgs/{org.Id}/projects",
            new { name = $"Project {suffix}", assignedTeamId = team.Id });

        return new Workspace(owner, org.Id, team.Id, project.Id);
    }

    /// <summary>Adds a work item to the project, assigned to the owner.</summary>
    public async Task<Guid> AddWorkItemAsync(string title, string type = "Task")
    {
        var item = await Owner.Post<Created>($"/api/projects/{ProjectId}/workitems",
            new { title, type, teamId = TeamId, assigneeId = Owner.UserId });

        return item.Id;
    }

    /// <summary>Registers a new user and adds them to this organization as a plain member.</summary>
    /// <remarks>
    /// The interesting principal in most authorization tests: they belong to the organization, hold
    /// <c>org:read</c>, and have no team or project grant at all.
    /// </remarks>
    public async Task<TestApi> AddOrganizationMemberAsync(BoardSyncApiFactory factory)
    {
        var member = await TestApi.RegisterAsync(factory);
        await Owner.Post($"/api/orgs/{OrganizationId}/members", new { userId = member.UserId });
        return member;
    }

    private sealed record Created(Guid Id);
}
