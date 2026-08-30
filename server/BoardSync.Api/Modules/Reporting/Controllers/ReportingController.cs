using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Reporting.DTOs;
using BoardSync.Api.Modules.Reporting.Services;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Reporting.Controllers;

/// <summary>
/// Computed delivery metrics: burndown, velocity, cycle time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number these endpoints return is computed from recorded facts</b>, and that is a
/// deliberate boundary rather than an implementation detail. The Intelligence module will narrate
/// over these figures; it will not produce them. A model asked to both compute and narrate returns
/// plausible numbers, and nobody downstream can tell which were which.
/// </para>
/// <para>
/// Gated on reading, not administering. A burndown is something a team looks at together, and
/// putting it behind project administration would make the people doing the work ask somebody else
/// how it is going.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Produces("application/json")]
public class ReportingController : ControllerBase
{
    private readonly IReportingService _reporting;

    public ReportingController(IReportingService reporting)
    {
        _reporting = reporting;
    }

    /// <summary>
    /// Burndown, cycle time and delivery figures for one sprint. Requires <c>sprint:read</c>.
    /// </summary>
    /// <remarks>
    /// The burndown is recomputed from work item history rather than read from nightly snapshots, so
    /// it is correct for sprints that ran before this endpoint existed and cannot be wrong because a
    /// job did not run.
    /// </remarks>
    [HttpGet("api/sprints/{sprintId:guid}/report")]
    [RequirePermission(Permissions.SprintRead, From = "sprintId")]
    [ProducesResponseType(typeof(ApiResponse<SprintReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSprintReport(Guid sprintId, CancellationToken ct)
    {
        var report = await _reporting.GetSprintReportAsync(sprintId, ct);

        return Ok(new ApiResponse<SprintReport>(true, "Sprint report retrieved.", report));
    }

    /// <summary>
    /// Velocity across completed sprints, and the project's cycle time. Requires <c>project:read</c>.
    /// </summary>
    /// <param name="projectId">The project.</param>
    /// <param name="sprints">
    /// How many completed sprints to include, most recent first, then returned oldest-first. Clamped
    /// rather than rejected.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Completed sprints only — an in-flight sprint's completed points are a partial number, and
    /// charting it makes the newest bar look like a collapse to anybody who opens the page mid-sprint.
    /// </remarks>
    [HttpGet("api/teams/{teamId:guid}/reports/velocity")]
    [RequirePermission(Permissions.TeamRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<VelocityReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeamVelocity(
        Guid teamId, [FromQuery] int sprints = 6, CancellationToken ct = default)
    {
        var report = await _reporting.GetTeamVelocityAsync(teamId, sprints, ct);

        return Ok(new ApiResponse<VelocityReport>(true, "Velocity retrieved.", report));
    }

    /// <summary>
    /// Velocity for the team that builds this project. Requires <c>project:read</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same figures as the team route, reached from a project. Kept because it is the question
    /// somebody on a project page is actually asking — "how fast does the team building this move"
    /// — and making every client resolve the owning team first would be a round trip for something
    /// one join answers.
    /// </para>
    /// <para>
    /// <b>It is not a per-project velocity.</b> A sprint spans the team's projects, so there is no
    /// such number; this is the team's, labelled honestly by the client.
    /// </para>
    /// </remarks>
    [HttpGet("api/projects/{projectId:guid}/reports/velocity")]
    [RequirePermission(Permissions.ProjectRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<VelocityReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVelocityForProject(
        Guid projectId, [FromQuery] int sprints = 6, CancellationToken ct = default)
    {
        var report = await _reporting.GetVelocityForProjectAsync(projectId, sprints, ct);

        return Ok(new ApiResponse<VelocityReport>(true, "Velocity retrieved.", report));
    }

    /// <summary>
    /// How this project's work has been spread across states, day by day. Requires
    /// <c>project:read</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reconstructed from <c>WorkItemHistory</c> like every other figure here — nothing is
    /// snapshotted, so the series is correct for days before this endpoint existed.
    /// </para>
    /// <para>
    /// Project-wide rather than sprint-scoped, and deliberately: cumulative flow shows where work
    /// piles up over weeks, and a two-week sprint is too short a window for a queue to become
    /// visible. The burndown is the sprint-scoped chart.
    /// </para>
    /// </remarks>
    [HttpGet("api/projects/{projectId:guid}/reports/cumulative-flow")]
    [RequirePermission(Permissions.ProjectRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<CumulativeFlowReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCumulativeFlow(
        Guid projectId, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        var report = await _reporting.GetCumulativeFlowAsync(projectId, days, ct);

        return Ok(new ApiResponse<CumulativeFlowReport>(
            true, "Cumulative flow retrieved.", report));
    }
}
