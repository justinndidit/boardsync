using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public class BoardSyncDbContext : DbContext
{
  public BoardSyncDbContext(DbContextOptions<BoardSyncDbContext> options) : base(options)
  {
  }
}
