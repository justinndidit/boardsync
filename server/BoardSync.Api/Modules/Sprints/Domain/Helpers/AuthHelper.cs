using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Kernel.Exceptions;


namespace BoardSync.Api.Modules.Sprints.Domain.Helpers;

public class AuthHelpers : IAuthHelpers
{
  private readonly IRbacService _rbac;
  private readonly ICurrentUserContext _currentUser;


  public AuthHelpers(IRbacService rbac,ICurrentUserContext currentUser)
  {
    _rbac = rbac;
    _currentUser = currentUser;
  }

    public async Task RequireProjectAsync(Guid projectId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();
    }
}