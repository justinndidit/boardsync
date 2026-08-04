using System;

namespace BoardSync.Api.Shared.Auth;

public interface  ICurrentUserContext
{
  Guid UserId {get;}
  bool IsAuthenticated {get;}
  string Email {get;}
}
