using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Tests;

/// <summary>
/// That <see cref="ProjectVisibility.Predicate"/> reaches the database in the shape it was designed
/// to have.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests next door prove the visibility set is <em>correct</em>. They cannot prove it is
/// <em>usable</em>: an expression tree that EF cannot translate compiles, passes every pure test, and
/// then throws at runtime on the first search — or, worse, silently evaluates client-side, pulling
/// every project in the database into memory to filter it there.
/// </para>
/// <para>
/// So these assert the generated SQL. No database is involved — <c>ToQueryString</c> compiles the
/// query against the Npgsql provider without opening a connection, which is why this belongs in the
/// unit test project rather than waiting for the integration harness.
/// </para>
/// </remarks>
public class ProjectVisibilityTranslationTests
{
    private static BoardSyncDbContext Context() =>
        new(new DbContextOptionsBuilder<BoardSyncDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-only;Username=none;Password=none")
            .Options);

    private static ProjectVisibility SomeVisibility() =>
        new([Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]);

    /// <summary>
    /// Each of the three routes becomes <c>= ANY(@parameter)</c> — one bound array parameter, not an
    /// inlined list of ids.
    /// </summary>
    /// <remarks>
    /// This is the property that keeps the query plan cacheable. An inlined list would produce
    /// different SQL text for every distinct number of grants a caller holds, so Postgres would plan
    /// each variant separately, and a user with a hundred grants would ship a hundred literals on
    /// every keystroke of a search box.
    /// </remarks>
    [Fact]
    public void EachRouteBecomesABoundArrayParameter()
    {
        using var context = Context();

        var sql = context.Projects.Where(SomeVisibility().Predicate()).ToQueryString();

        Assert.Contains("\"Id\" = ANY (", sql);
        Assert.Contains("\"AssignedTeamId\" = ANY (", sql);
        Assert.Contains("\"OrganizationId\" = ANY (", sql);
    }

    /// <summary>
    /// The readable-project set stays a subquery of the query that needs it, rather than a round trip
    /// whose result is shipped back as an <c>IN</c> list.
    /// </summary>
    /// <remarks>
    /// The shape every caller composes: resolve visible projects as an unmaterialized
    /// <see cref="IQueryable{T}"/>, then filter the real table by it. Both halves must land in one
    /// statement.
    /// </remarks>
    [Fact]
    public void VisibleProjectsComposesAsASubqueryNotARoundTrip()
    {
        using var context = Context();

        var visibleProjectIds = context.Projects
            .Where(SomeVisibility().Predicate())
            .Where(p => p.IsActive)
            .Select(p => p.Id);

        var sql = context.WorkItems
            .Where(w => visibleProjectIds.Contains(w.ProjectId) && w.IsActive)
            .ToQueryString();

        Assert.Contains("FROM work.\"WorkItems\"", sql);

        // Projects is reached only from inside the work item query's WHERE clause — that is what
        // makes it a subquery rather than a separately-executed lookup whose ids were pasted in.
        var where = sql.IndexOf("WHERE", StringComparison.Ordinal);
        var projects = sql.IndexOf("FROM org.\"Projects\"", StringComparison.Ordinal);

        Assert.True(projects > where && where >= 0,
            $"Expected org.\"Projects\" to appear inside the WHERE clause as a subquery.\n{sql}");

        // And it is one statement, so it costs one round trip.
        Assert.DoesNotContain(";", sql);
    }

    /// <summary>
    /// Nothing is evaluated on the client. If EF ever gave up on part of this predicate it would
    /// filter in memory after loading the table, which is a correctness-preserving change that
    /// destroys the point of the design.
    /// </summary>
    [Fact]
    public void TheWholePredicateIsTranslated()
    {
        using var context = Context();

        var sql = context.Projects.Where(SomeVisibility().Predicate()).ToQueryString();

        // Every column the predicate names appears in the WHERE clause. A route that failed to
        // translate would simply be absent from the SQL.
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        Assert.Contains("\"Id\"", where);
        Assert.Contains("\"AssignedTeamId\"", where);
        Assert.Contains("\"OrganizationId\"", where);
    }
}
