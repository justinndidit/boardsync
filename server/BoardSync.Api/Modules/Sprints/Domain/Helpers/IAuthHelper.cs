using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Kernel.Exceptions;


namespace BoardSync.Api.Modules.Sprints.Domain.Helpers;

public interface IAuthHelpers
{
    Task RequireProjectRoleAsync(Guid projectId, RoleType minimum, CancellationToken ct);
}