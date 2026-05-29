using DoTogether.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Middleware;

/// <summary>
/// Translates known exception types into RFC 9457 Problem Details responses.
/// Production responses do NOT include raw exception messages (information disclosure).
/// </summary>
public class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IWebHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request cancelled by client on {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                logger.LogError(ex, "Exception after response started on {Method} {Path}", context.Request.Method, context.Request.Path);
                throw;
            }
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (status, title, errorCode, detail, extensions) = MapException(exception);

        // In production, do not leak unmapped raw .NET exception messages.
        if (status == StatusCodes.Status500InternalServerError && !env.IsDevelopment())
        {
            detail = "An unexpected error occurred.";
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        problem.Extensions["code"] = errorCode;
        if (env.IsDevelopment() && status == StatusCodes.Status500InternalServerError)
            problem.Extensions["debug"] = exception.ToString();

        foreach (var pair in extensions)
            problem.Extensions[pair.Key] = pair.Value;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    }

    private static (int Status, string Title, string Code, string Detail, IReadOnlyDictionary<string, object?> Extensions) MapException(Exception exception)
    {
        var noExt = (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();

        return exception switch
        {
            ApiProblemException apiProblem => (apiProblem.StatusCode, apiProblem.Title, apiProblem.ErrorCode, apiProblem.Message, apiProblem.Extensions),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized", "unauthorized", "Authentication required.", noExt),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict", "concurrency_conflict", "The resource was modified by another request. Please refresh and try again.", noExt),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request", "bad_request", exception.Message, noExt),
            InvalidTimeZoneException => (StatusCodes.Status400BadRequest, "Bad Request", "invalid_timezone", exception.Message, noExt),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found", "not_found", exception.Message, noExt),
            // Note: InvalidOperationException intentionally falls through to 500 — services should
            // raise ApiProblemException for expected business-logic failures.
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error", "internal_server_error", exception.Message, noExt)
        };
    }
}
