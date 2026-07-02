using BoardSync.Api.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

//db connection string
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var CORSPolicy = "DefaultPolicy";
var configuredOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
var allowedOrigins = configuredOrigins.Length > 0
    ? configuredOrigins
    : ["http://localhost:5173", "https://localhost:5173"];

//Dependency Injection
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

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

// ef core setup
builder.Services.AddDbContext<BoardSyncDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null
        );
    });
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

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

// Configure the HTTP request pipeline.
app.UseCors(CORSPolicy);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseCors(CORSPolicy);

app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapControllers();

app.Run();
