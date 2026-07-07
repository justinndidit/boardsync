using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.DTOs;
using BoardSync.Api.Modules.OrgProject.Events;
using BoardSync.Api.Modules.OrgProject.Models;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Services;

public class TeamService : ITeamService
{
    private readonly BoardSyncDbContext _context;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<TeamService> _logger;

    public TeamService(
        BoardSyncDbContext context,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<TeamService> logger)
    {
        _context = context;
        _rbac = rbac;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<TeamResponse> CreateAsync(
        Guid projectId,
        CreateTeamRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            throw new NotFoundException("Project", projectId);

        var team = new Team
        {
            ProjectId = projectId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync(ct);

        // Creator joins team as TeamMember
        await AddMemberInternalAsync(team, createdBy, createdBy, ct);
        await _rbac.AssignRoleAsync(createdBy, RoleType.TeamMember, RoleScope.Team, team.Id, createdBy, ct);

        await _eventBus.PublishAsync(new TeamCreated(team.Id, projectId, team.Name, createdBy), ct);

        _logger.LogInformation("Team '{Name}' ({Id}) created in project {ProjectId} by {UserId}",
            team.Name, team.Id, projectId, createdBy);

        return await MapToResponseAsync(team, ct);
    }

    public async Task<TeamResponse> GetByIdAsync(Guid teamId, CancellationToken ct = default)
    {
        var team = await _context.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        return MapToResponse(team);
    }

    public async Task<PagedResult<TeamResponse>> GetForProjectAsync(
        Guid projectId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var query = _context.Teams
            .Include(t => t.Members)
            .Where(t => t.ProjectId == projectId && t.IsActive)
            .OrderBy(t => t.Name);

        var total = await query.CountAsync(ct);
        var items = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);
        return new PagedResult<TeamResponse>(items.Select(MapToResponse).ToList(), total, pagination.Page, pagination.PageSize);
    }

    public async Task<TeamResponse> UpdateAsync(
        Guid teamId,
        UpdateTeamRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var team = await _context.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        team.Name = request.Name.Trim();
        team.Description = request.Description?.Trim() ?? team.Description;
        team.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return MapToResponse(team);
    }

    public async Task<TeamMemberResponse> AddMemberAsync(
        Guid teamId,
        Guid userId,
        Guid addedBy,
        CancellationToken ct = default)
    {
        var team = await _context.Teams
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        var user = await _context.Users.FindAsync([userId], ct)
            ?? throw new NotFoundException("User", userId);

        var exists = await _context.TeamMemberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
        if (!exists)
        {
            await AddMemberInternalAsync(team, userId, addedBy, ct);
            await _rbac.AssignRoleAsync(userId, RoleType.TeamMember, RoleScope.Team, teamId, addedBy, ct);
        }

        await _eventBus.PublishAsync(new MemberAddedToTeam(teamId, team.ProjectId, userId, addedBy), ct);

        var membership = await _context.TeamMemberships.FirstAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
        return new TeamMemberResponse(userId, user.DisplayName, user.Email, user.ProfilePictureUrl, membership.JoinedAt);
    }

    public async Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _context.TeamMemberships
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

        if (membership != null)
        {
            _context.TeamMemberships.Remove(membership);
            await _context.SaveChangesAsync(ct);
            await _eventBus.PublishAsync(new MemberRemovedFromTeam(teamId, userId), ct);
        }
    }

    public async Task<PagedResult<TeamMemberResponse>> GetMembersAsync(
        Guid teamId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (!await _context.Teams.AnyAsync(t => t.Id == teamId, ct))
            throw new NotFoundException(nameof(Team), teamId);

        var query = _context.TeamMemberships
            .Where(m => m.TeamId == teamId)
            .Join(_context.Users,
                m => m.UserId,
                u => u.Id,
                (m, u) => new TeamMemberResponse(u.Id, u.DisplayName, u.Email, u.ProfilePictureUrl, m.JoinedAt))
            .OrderBy(r => r.DisplayName);

        var total = await query.CountAsync(ct);
        var items = await query.Skip(pagination.Skip).Take(pagination.PageSize).ToListAsync(ct);
        return new PagedResult<TeamMemberResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    // -------------------------------------------------------------------------
    private async Task AddMemberInternalAsync(Team team, Guid userId, Guid addedBy, CancellationToken ct)
    {
        _context.TeamMemberships.Add(new TeamMembership
        {
            TeamId = team.Id,
            UserId = userId,
            CreatedBy = addedBy
        });
        await _context.SaveChangesAsync(ct);
    }

    private static TeamResponse MapToResponse(Team t) =>
        new(t.Id, t.ProjectId, t.Name, t.Description, t.IsActive, t.Members.Count, t.CreatedAt);

    private async Task<TeamResponse> MapToResponseAsync(Team t, CancellationToken ct)
    {
        var memberCount = await _context.TeamMemberships.CountAsync(m => m.TeamId == t.Id, ct);
        return new(t.Id, t.ProjectId, t.Name, t.Description, t.IsActive, memberCount, t.CreatedAt);
    }
}
