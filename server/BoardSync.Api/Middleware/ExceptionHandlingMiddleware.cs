using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Security;
using System.Security.Authentication;
using System.Text.Json;

namespace BoardSync.Api.Middleware;

public class GlobalExceptionHandlingMiddleware
{
    /// <summary>PostgreSQL SQLSTATE 23505 — unique_violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>PostgreSQL SQLSTATE 23503 — foreign_key_violation.</summary>
    private const string ForeignKeyViolation = "23503";

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;
    private readonly IWebHostEnvironment _environment;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate next, 
        ILogger<GlobalExceptionHandlingMiddleware> logger,
        IWebHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var requestId = context.Items["RequestId"]?.ToString() ?? Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "Unhandled exception in request {RequestId}: {Message}", requestId, ex.Message);
            await HandleExceptionAsync(context, ex, requestId);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception, string requestId)
    {
        var response = context.Response;
        response.ContentType = "application/json";

        var (statusCode, message, shouldExposeDetails) = GetErrorDetails(exception);
        response.StatusCode = statusCode;

        var errorResponse = new ErrorResponse(
            Message: message,
            StatusCode: statusCode,
            Details: shouldExposeDetails && _environment.IsDevelopment() ? exception.Message : null
        );

        // Add request ID for tracking
        response.Headers.Append("X-Request-ID", requestId);

        var jsonResponse = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await response.WriteAsync(jsonResponse);
    }

    private (int statusCode, string message, bool shouldExposeDetails) GetErrorDetails(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => (401, "Unauthorized access", false),
            AuthenticationException => (401, "Authentication failed", false),
            SecurityException => (403, "Access forbidden", false),
            Shared.Kernel.Exceptions.ForbiddenException => (403, "Access forbidden", false),
            Shared.Kernel.Exceptions.NotFoundException => (404, exception.Message, false),
            Shared.Kernel.Exceptions.ConflictException => (409, exception.Message, true),
            Shared.Kernel.Exceptions.BusinessRuleException => (422, exception.Message, true),
            Shared.Kernel.Exceptions.DomainException => (400, exception.Message, true),
            KeyNotFoundException => (404, "Resource not found", false),
            ArgumentNullException => (400, "Invalid request - missing required data", true),
            ArgumentException => (400, "Invalid request parameters", true),
            InvalidOperationException => (400, "Invalid operation", true),
            NotSupportedException => (400, "Operation not supported", false),
            TimeoutException => (408, "Request timeout", false),
            TaskCanceledException => (408, "Request timeout", false),
            
            // Constraint violations. A lost check-then-insert race (two callers claiming the same
            // slug at once) is a conflict, not a server fault, and must not be reported as a 500.
            Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: UniqueViolation } } =>
                (409, "The resource already exists.", false),
            Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: ForeignKeyViolation } } =>
                (400, "The request references a resource that does not exist.", false),

            // Database and infrastructure errors
            Microsoft.EntityFrameworkCore.DbUpdateException => (500, "A data error occurred", false),
            System.Data.Common.DbException => (500, "A database error occurred", false),
            
            // Rate limiting
            _ when exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) => 
                (429, "Rate limit exceeded", false),
                
            _ => (500, "An internal server error occurred", false)
        };
    }
}

public static class ExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GlobalExceptionHandlingMiddleware>();
    }
}