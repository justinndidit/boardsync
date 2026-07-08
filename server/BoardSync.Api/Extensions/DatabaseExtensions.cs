using BoardSync.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Extensions;

public static class DatabaseExtensions
{
    public static async Task<WebApplication> MigrateDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<BoardSyncDbContext>>();

        try
        {
            logger.LogInformation("Starting database migration...");

            // Apply any pending migrations
            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                logger.LogInformation("Applying {Count} pending migrations: {Migrations}",
                    pendingMigrations.Count(),
                    string.Join(", ", pendingMigrations));

                await context.Database.MigrateAsync();
                logger.LogInformation("Database migration completed successfully");
            }
            else
            {
                logger.LogInformation("Database is up to date, no migrations needed");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while migrating the database");

            // In production, you might want to fail fast
            if (app.Environment.IsProduction())
            {
                throw;
            }

            // In development, log the error but continue
            logger.LogWarning("Database migration failed in development environment, continuing...");
        }

        return app;
    }

    public static async Task SeedDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<BoardSyncDbContext>>();

        try
        {
            logger.LogInformation("Starting database seeding...");

            // Add any initial seed data here
            // For example, default admin user, roles, etc.

            await context.SaveChangesAsync();
            logger.LogInformation("Database seeding completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database");
        }
    }
}