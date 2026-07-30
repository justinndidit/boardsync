using BoardSync.Api.Data;
using BoardSync.Api.Extensions;
using BoardSync.Api.Middleware;
using BoardSync.Api.Modules.OrgProject.Services.Implementations;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Modules.WorkItems.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Handlers;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Auth.Services.Implementations;
using BoardSync.Api.Shared.Kernel.Configuration;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

//db connection string
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var CORSPolicy = "DefaultPolicy";
var configuredOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
var allowedOrigins = configuredOrigins.Length > 0
    ? configuredOrigins
    : ["http://localhost:5173", "https://localhost:5173"];

if (builder.Environment.IsProduction() && configuredOrigins.Length == 0)
{
    throw new InvalidOperationException("AllowedOrigins must be explicitly configured in production.");
}

//Dependency Injection
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Add OpenAPI/Swagger services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "BoardSync API",
        Version = "v1"
    });

    // 1. Define the Security Scheme using the flat-mapped types
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer", // RFC-compliant lowercase scheme representation
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme. Enter your token below."
    });

    // 2. Add Security Requirement using .NET 10 OpenApiSecuritySchemeReference
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        });
});

// Authentication Configuration
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.Configure<SecuritySettings>(builder.Configuration.GetSection("SecuritySettings"));

// Authentication Services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();

// Shared Kernel — Event Bus
builder.Services.AddScoped<IEventBus, InMemoryEventBus>();

// RBAC Module
builder.Services.AddScoped<IRbacService, RbacService>();

// OrgProject Module
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<ITeamService, TeamService>();

// WorkItems Module
builder.Services.AddScoped<IWorkItemRepository, WorkItemRepository>();
builder.Services.AddScoped<IWorkItemService, WorkItemService>();

// Sprints / Boards Module
builder.Services.AddScoped<ISprintService, SprintService>();
builder.Services.AddScoped<IBoardService, BoardService>();

// Add HTTP Context Accessor
builder.Services.AddHttpContextAccessor();

// Configure Rate Limiting
builder.Services.Configure<RateLimitSettings>(builder.Configuration.GetSection("RateLimiting"));
var rateLimitSettings = builder.Configuration.GetSection("RateLimiting").Get<RateLimitSettings>() ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    // Each policy is partitioned per caller, so one caller can never consume another's budget.
    options.AddPolicy("api", ctx => PartitionFor(ctx, rateLimitSettings.Api, rateLimitSettings.Enabled));
    options.AddPolicy("auth", ctx => PartitionFor(ctx, rateLimitSettings.Auth, rateLimitSettings.Enabled));
    options.AddPolicy("password", ctx => PartitionFor(ctx, rateLimitSettings.Password, rateLimitSettings.Enabled));

    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/json";

        // Tell the client when it is worth trying again instead of making it guess.
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : (int)Math.Ceiling(TimeSpan.FromSeconds(rateLimitSettings.Auth.WindowSeconds).TotalSeconds);

        response.Headers.RetryAfter = retryAfterSeconds.ToString();

        var payload = new ErrorResponse(
            Message: $"Rate limit exceeded. Please retry in {retryAfterSeconds} second(s).",
            StatusCode: StatusCodes.Status429TooManyRequests);

        await response.WriteAsJsonAsync(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }, cancellationToken);
    };
});

// JWT Authentication
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>() ?? new JwtSettings();

// Validate JWT settings at startup
ValidateJwtSettings(jwtSettings, builder.Environment);

// Validate Security settings at startup
var securitySettings = builder.Configuration.GetSection("SecuritySettings").Get<SecuritySettings>() ?? new SecuritySettings();
ValidateSecuritySettings(securitySettings, builder.Environment);

var key = Encoding.ASCII.GetBytes(jwtSettings.Secret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = jwtSettings.ValidateIssuerSigningKey,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = jwtSettings.ValidateIssuer,
        ValidIssuer = jwtSettings.Issuer,
        ValidateAudience = jwtSettings.ValidateAudience,
        ValidAudience = jwtSettings.Audience,
        ValidateLifetime = jwtSettings.ValidateLifetime,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization(options =>
{
    // Default authenticated policy
    options.AddPolicy("RequireAuthentication", policy =>
        policy.RequireAuthenticatedUser());

    // Email confirmed policy
    options.AddPolicy("RequireEmailConfirmed", policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new EmailConfirmedRequirement()));

    // Active user policy
    options.AddPolicy("RequireActiveUser", policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new ActiveUserRequirement()));

    // Ownership policy
    options.AddPolicy("RequireOwnership", policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new OwnershipRequirement()));

    // Default policy requires authentication
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Fallback policy for endpoints without explicit authorization
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Register authorization handlers
builder.Services.AddScoped<IAuthorizationHandler, EmailConfirmedHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveUserHandler>();
builder.Services.AddScoped<IAuthorizationHandler, OwnershipHandler>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

//postgres connection pool configuration
dataSourceBuilder.ConnectionStringBuilder.Pooling = true;
dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 10;
dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 100;
dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 300; // 5 minutes

var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);

// EF core setup
builder.Services.AddDbContext<BoardSyncDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null
        );
        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "public");
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy(CORSPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Auto-migrate database on startup
await app.MigrateDatabaseAsync();

// Configure the HTTP request pipeline.

// Add Swagger UI middleware (before authentication for public access)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "BoardSync API v1");
        c.RoutePrefix = "swagger";
        c.DocumentTitle = "BoardSync API Documentation";
        c.DisplayRequestDuration();
    });
}

// Exception handling (should be first in pipeline)
app.UseGlobalExceptionHandler();

// Forwarded headers must be very early for proxy deployments
app.UseForwardedHeaders();

// Security and logging middleware (after forwarded headers)
app.UseSecurityHeaders();
app.UseRequestLogging();

app.UseCors(CORSPolicy);

// HTTPS redirection and HSTS
app.UseHttpsRedirection();
if (app.Environment.IsProduction())
{
    app.UseHsts();
}

app.UseAuthentication();

// Must run after authentication: rate limit partitions are keyed on the authenticated
// user when one is present, which requires HttpContext.User to be populated.
app.UseRateLimiter();

app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapControllers();

app.Run();

/// <summary>
/// Builds a rate limit partition for a single caller. Authenticated requests are keyed by user ID
/// so that users sharing an IP (NAT, office network, mobile carrier) get independent budgets;
/// anonymous requests fall back to the client IP.
/// </summary>
static RateLimitPartition<string> PartitionFor(HttpContext httpContext, RateLimitPolicySettings policy, bool enabled)
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    var partitionKey = !string.IsNullOrEmpty(userId)
        ? $"user:{userId}"
        : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    if (!enabled)
        return RateLimitPartition.GetNoLimiter(partitionKey);

    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        Window = TimeSpan.FromSeconds(policy.WindowSeconds),
        PermitLimit = policy.PermitLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = policy.QueueLimit
    });
}

static void ValidateJwtSettings(JwtSettings jwtSettings, IWebHostEnvironment environment)
{
    var errors = new List<string>();

    // Validate secret
    if (string.IsNullOrWhiteSpace(jwtSettings.Secret))
    {
        errors.Add("JWT Secret is required");
    }
    else if (jwtSettings.Secret.Length < 32)
    {
        errors.Add("JWT Secret must be at least 32 characters long");
    }
    else if (environment.IsProduction() &&
             (jwtSettings.Secret.Contains("${") ||
              jwtSettings.Secret.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
              jwtSettings.Secret.Contains("change", StringComparison.OrdinalIgnoreCase) ||
              jwtSettings.Secret.Contains("example", StringComparison.OrdinalIgnoreCase)))
    {
        errors.Add("JWT Secret appears to contain placeholder values in production");
    }

    // Validate issuer and audience
    if (string.IsNullOrWhiteSpace(jwtSettings.Issuer))
    {
        errors.Add("JWT Issuer is required");
    }

    if (string.IsNullOrWhiteSpace(jwtSettings.Audience))
    {
        errors.Add("JWT Audience is required");
    }

    // Validate expiration times
    if (jwtSettings.AccessTokenExpirationMinutes <= 0)
    {
        errors.Add("Access token expiration must be positive");
    }

    if (jwtSettings.RefreshTokenExpirationDays <= 0)
    {
        errors.Add("Refresh token expiration must be positive");
    }

    if (errors.Any())
    {
        var errorMessage = "JWT Configuration validation failed:\n" + string.Join("\n", errors.Select(e => $"- {e}"));
        throw new InvalidOperationException(errorMessage);
    }
}

static void ValidateSecuritySettings(SecuritySettings securitySettings, IWebHostEnvironment environment)
{
    var errors = new List<string>();

    if (environment.IsProduction())
    {
        // Enforce strong security defaults in production
        if (securitySettings.MinPasswordLength < 8)
        {
            errors.Add("Minimum password length must be at least 8 characters in production");
        }

        if (!securitySettings.RequireEmailConfirmation)
        {
            errors.Add("Email confirmation must be required in production");
        }

        if (securitySettings.MaxFailedAccessAttempts > 5)
        {
            errors.Add("Maximum failed access attempts should not exceed 5 in production");
        }

        if (securitySettings.AccountLockoutMinutes < 15)
        {
            errors.Add("Account lockout duration should be at least 15 minutes in production");
        }
    }

    // Basic validation for all environments
    if (securitySettings.MinPasswordLength < 4)
    {
        errors.Add("Minimum password length must be at least 4 characters");
    }

    if (securitySettings.MaxFailedAccessAttempts < 1)
    {
        errors.Add("Maximum failed access attempts must be at least 1");
    }

    if (securitySettings.AccountLockoutMinutes < 1)
    {
        errors.Add("Account lockout duration must be at least 1 minute");
    }

    if (errors.Any())
    {
        var errorMessage = "Security Configuration validation failed:\n" + string.Join("\n", errors.Select(e => $"- {e}"));
        throw new InvalidOperationException(errorMessage);
    }
}
