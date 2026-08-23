using System.Linq.Expressions;
using BoardSync.Api.Modules.OrgProject.Domain.Models;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Which projects a user may do one thing to, expressed as the grants that reach them rather than as
/// a list of project ids.
/// </summary>
/// <remarks>
/// <para>
/// This is the set-shaped counterpart to <see cref="Services.AccessEvaluator.GrantsAtProject"/>.
/// That answers "may they, here?" for one project whose position in the tree is already known; this
/// answers "which ones?" for a query that has not loaded any projects yet, and must not have to.
/// </para>
/// <para>
/// <b>Why not just return the project ids.</b> Expanding the grants would mean querying every project
/// in every organization the user administers, shipping the ids back, and then sending them to the
/// server again as an <c>IN</c> list on the real query. That list grows with the size of the
/// organization rather than the size of the user's access — an OrgAdmin of a thousand-project
/// organization would carry a thousand GUIDs through every search. It is the same reason
/// <see cref="AccessSnapshot"/> stores grants and not their consequences.
/// </para>
/// <para>
/// Keeping the three grant sets instead means each one is bounded by what the user was actually
/// given, and the expansion happens inside Postgres as an indexed predicate over
/// <c>Projects</c> — see <see cref="Predicate"/>. The arrays are almost always tiny; the projects
/// they select for may not be.
/// </para>
/// <para>
/// Built by <see cref="Services.AccessEvaluator.VisibleProjects"/>, which is pure, so a visibility
/// set is a value derived from a snapshot and carries no lifetime of its own.
/// </para>
/// </remarks>
/// <param name="OrganizationIds">
/// Organizations where a directly-held role carries the permission onto everything inside — in
/// practice OrgAdmin, since it is the only organization role that reaches below itself.
/// </param>
/// <param name="TeamIds">
/// Teams whose grant carries the permission onto the projects assigned to them, through the
/// team → project edge.
/// </param>
/// <param name="ProjectIds">Projects where the permission is held by a direct project-scope grant.</param>
public sealed record ProjectVisibility(
    Guid[] OrganizationIds,
    Guid[] TeamIds,
    Guid[] ProjectIds)
{
    /// <summary>A user who may not do this to any project at all.</summary>
    public static ProjectVisibility None { get; } = new([], [], []);

    /// <summary>
    /// Whether this reaches no project whatsoever, so a caller can skip the query rather than run one
    /// that cannot match.
    /// </summary>
    public bool IsEmpty =>
        OrganizationIds.Length == 0 && TeamIds.Length == 0 && ProjectIds.Length == 0;

    /// <summary>
    /// The three routes as one predicate over <see cref="Project"/>, for composing into a query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended to stay an <see cref="IQueryable{T}"/> subquery rather than being materialized:
    /// </para>
    /// <code>
    /// var visible = _context.Projects.Where(visibility.Predicate()).Select(p => p.Id);
    /// return await _context.WorkItems.Where(w => visible.Contains(w.ProjectId)).ToListAsync(ct);
    /// </code>
    /// <para>
    /// Npgsql renders each <c>Contains</c> over a captured array as <c>= ANY(@p)</c> — one bound
    /// parameter, not an inlined list, so the SQL text is identical whatever the user holds and the
    /// plan is cacheable.
    /// </para>
    /// <para>
    /// Note this deliberately says nothing about <c>IsActive</c>. Visibility and liveness are
    /// different questions and every caller wants a different answer to the second one.
    /// </para>
    /// </remarks>
    public Expression<Func<Project, bool>> Predicate()
    {
        // Captured as locals so the expression closes over three arrays rather than over `this`,
        // which keeps what EF has to evaluate client-side down to a plain closure field read.
        var organizations = OrganizationIds;
        var teams = TeamIds;
        var projects = ProjectIds;

        return p => projects.Contains(p.Id)
                 || teams.Contains(p.AssignedTeamId)
                 || organizations.Contains(p.OrganizationId);
    }

    /// <summary>
    /// Whether one project, whose position in the tree is known, is in this set.
    /// </summary>
    /// <remarks>
    /// The in-memory twin of <see cref="Predicate"/>. It exists so the set-based answer and the
    /// single-project answer can be asserted equal against each other — see
    /// <c>ProjectVisibilityTests</c>. If these two ever disagree, one of the two authorization paths
    /// is wrong and the test says which.
    /// </remarks>
    public bool Includes(Guid projectId, ProjectLocation location) =>
        ProjectIds.Contains(projectId)
        || TeamIds.Contains(location.AssignedTeamId)
        || OrganizationIds.Contains(location.OrganizationId);
}
