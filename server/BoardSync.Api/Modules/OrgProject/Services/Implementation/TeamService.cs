using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Implementations;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.VisualBasic;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

public class TeamService : ITeamService
{
    private readonly ITeamRepository _teamRepo;
    private readonly ITeamMembershipRepository _teamMembershipRepo;
    private readonly IProjectRepository _projectRepo;
    private readonly IRbacService _rbac;
    private readonly IUserService _userService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<TeamService> _logger;

    public TeamService(
        ITeamRepository teamRepository,
        ITeamMembershipRepository teamMembershipRepository,
        IProjectRepository projectRepository,
        IRbacService rbac,
        IUserService userService,
        IEventBus eventBus,
        ILogger<TeamService> logger)
    {
        _teamRepo = teamRepository;
        _teamMembershipRepo = teamMembershipRepository;
        _projectRepo = projectRepository;
        _rbac = rbac;
        _userService = userService;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<TeamResponse> CreateAsync(
        Guid orgId,
        CreateTeamRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var name = request.Name.Trim();

        // Team names are unique per project; check first so the collision surfaces as a 409
        // rather than a unique-index violation surfacing as a 500.
        var existingTeamByName = await _teamRepo.GetByNameAsync(name, ct);
        if (!(existingTeamByName == null))
            throw new ConflictException($"A team named '{name}' already exists in this project.");

        var team = new Team
        {
            OrganizationId = orgId,
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = createdBy
        };

        // Team and the creator's membership land in one save.
        _teamRepo.Add(team);
        _teamMembershipRepo.AddMembership(new TeamMembership
        {
            TeamId = team.Id,
            UserId = createdBy,
            CreatedBy = createdBy
        });
        await _teamRepo.SaveChangesAsync(ct);

        await _rbac.AssignRoleAsync(createdBy, RoleType.TeamMember, RoleScope.Team, team.Id, createdBy, ct);

        await _eventBus.PublishAsync(new TeamCreated(team.Id, orgId, team.Name, createdBy), ct);

        _logger.LogInformation("Team '{Name}' ({Id}) created in organization {OrganizationId} by {UserId}",
            team.Name, team.Id, orgId, createdBy);

        return await MapToResponseAsync(team, ct);
    }

    public async Task<TeamResponse> GetByIdAsync(Guid teamId, CancellationToken ct = default)
    {
        var team = await _teamRepo.GetActiveByIdAsync(teamId, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        return await MapToResponseAsync(team, ct);
    }

    public async Task<PagedResult<TeamResponse>> GetByOrgIdAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default)
    {
        var (teams, total) = await _teamRepo.GetActiveTeamsInOrgAsync(orgId, pagination, ct);
        return new PagedResult<TeamResponse>(teams, total, pagination.Page, pagination.PageSize);
    }

    public async Task<TeamResponse> UpdateAsync(
        Guid teamId,
        UpdateTeamRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var team = await _teamRepo.GetActiveByIdAsync(teamId, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        team.Name = request.Name.Trim();
        team.Description = request.Description?.Trim() ?? team.Description;
        team.UpdatedAt = DateTime.UtcNow;

        await _teamRepo.SaveChangesAsync(ct);
        return await MapToResponseAsync(team, ct);
    }

    public async Task<TeamMemberResponse> AddMemberAsync(
        Guid teamId,
        Guid userId,
        Guid addedBy,
        CancellationToken ct = default)
    {
        var team = await _teamRepo.GetActiveByIdAsync(teamId, ct)
            ?? throw new NotFoundException(nameof(Team), teamId);

        var user = await _userService.GetByIdAsync(userId);
        if (!user.Success || user.Data is null)
            throw new NotFoundException("User", userId);

        var membership = await _teamMembershipRepo.GetMembershipAsync(teamId, userId, ct);

        if (membership is null)
        {
            membership = new TeamMembership
            {
                TeamId = teamId,
                UserId = userId,
                CreatedBy = addedBy
            };

            _teamMembershipRepo.AddMembership(membership);
            await _teamRepo.SaveChangesAsync(ct);

            await _rbac.AssignRoleAsync(userId, RoleType.TeamMember, RoleScope.Team, teamId, addedBy, ct);
        }

        await _eventBus.PublishAsync(new MemberAddedToTeam(teamId, team.OrganizationId, userId, addedBy), ct);

        return new TeamMemberResponse(
            userId, user.Data.DisplayName, user.Data.Email, user.Data.ProfilePictureUrl, membership.JoinedAt);
    }

    public async Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _teamMembershipRepo.GetMembershipAsync(teamId, userId, ct);
        if (membership is null) return;

        _teamMembershipRepo.RemoveMembership(membership);
        await _teamRepo.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(new MemberRemovedFromTeam(teamId, userId), ct);
    }

    public async Task<PagedResult<TeamMemberResponse>> GetMembersAsync(
        Guid teamId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        if (!await _teamRepo.ExistsAsync(teamId, ct))
            throw new NotFoundException(nameof(Team), teamId);

        var (members, total) = await _teamMembershipRepo.GetMembersAsync(
            teamId, pagination.Skip, pagination.PageSize, ct);

        var items = members
            .Select(m => new TeamMemberResponse(
                m.UserId, m.DisplayName, m.Email, m.ProfilePictureUrl, m.JoinedAt))
            .ToList();

        return new PagedResult<TeamMemberResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    // -------------------------------------------------------------------------

    private async Task<TeamResponse> MapToResponseAsync(Team t, CancellationToken ct)
    {
        return new(t.Id, t.OrganizationId, t.Name, t.Description, t.IsActive, t.Members.Count, t.CreatedAt);
    }

  public async Task<bool> IsMember(Guid teamId, Guid userId, CancellationToken ct = default)
  {
    return await _teamMembershipRepo.IsMemberAsync(teamId, userId);
  }
}
