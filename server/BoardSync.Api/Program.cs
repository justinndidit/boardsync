using BoardSync.Api.Data;
using BoardSync.Api.Extensions;
using BoardSync.Api.Middleware;
using BoardSync.Api.Modules.OrgProject.Services;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.Handlers;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Auth.Services.Implementations;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text;
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
builder.Services.AddSwaggerGen();

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

// Add HTTP Context Accessor
builder.Services.AddHttpContextAccessor();

// Configure Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    // General API rate limiting
    options.AddFixedWindowLimiter("api", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.PermitLimit = 100;
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 10;
    });

    // Strict rate limiting for auth endpoints
    options.AddFixedWindowLimiter("auth", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.PermitLimit = 10; // 10 requests per minute for auth operations
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 2;
    });

    // Very strict for password operations
    options.AddFixedWindowLimiter("password", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(5);
        limiterOptions.PermitLimit = 3; // 3 attempts per 5 minutes
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });

    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Please try again later.");
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

// Add rate limiting middleware
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapControllers();

app.Run();

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
