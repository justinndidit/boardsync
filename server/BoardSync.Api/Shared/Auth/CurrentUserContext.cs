using System;
using System.Security.Claims;

namespace BoardSync.Api.Shared.Auth;

public class CurrentUserContext : ICurrentUserContext
{
  private readonly IHttpContextAccessor _httpContextAccessor;

  public CurrentUserContext(IHttpContextAccessor httpContextAccessor)
  {
      _httpContextAccessor = httpContextAccessor;
  }

  public bool IsAuthenticated =>
      _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;

  public Guid UserId =>
      Guid.TryParse(_httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
          ? id
          : Guid.Empty;

  public string Email =>
      _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
}
