using System.Text.Json;
using BloodLink.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace BloodLink.Api.Security;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (!context.Response.HasStarted && context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            {
                var isUnauthenticated = context.Response.StatusCode == StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
                {
                    Status = context.Response.StatusCode,
                    Title = isUnauthenticated ? "Authentication required" : "Forbidden",
                    Detail = isUnauthenticated ? "Sign in to access this resource." : "You are not authorized to perform this request.",
                    Extensions =
                    {
                        ["traceId"] = context.TraceIdentifier,
                        ["code"] = isUnauthenticated ? "unauthenticated" : "forbidden"
                    }
                }, cancellationToken: context.RequestAborted);
            }
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, title, detail, code) = exception switch
            {
                EntityNotFoundException or PrivateResourceNotFoundException
                    => (StatusCodes.Status404NotFound, "Resource not found", "The requested resource was not found.", "resource_not_found"),
                BloodLink.Domain.Exceptions.UnauthorizedAccessException or System.UnauthorizedAccessException
                    => (StatusCodes.Status403Forbidden, "Forbidden", "You are not authorized to perform this request.", "forbidden"),
                ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", "The request contains invalid values.", "validation_error"),
                ConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency conflict", "The resource changed. Refresh and try again.", "concurrency_conflict"),
                InsufficientInventoryException or InvalidFacilityStatusException or BusinessRuleViolationException
                    => (StatusCodes.Status409Conflict, "Request conflicts with current state", "The request conflicts with the current state.", "state_conflict"),
                InvalidOperationException => (StatusCodes.Status409Conflict, "Request conflicts with current state", "The request conflicts with the current state.", "state_conflict"),
                _ => (StatusCodes.Status500InternalServerError, "Request could not be completed", "A temporary server error prevented the request from completing.", "internal_error")
            };

            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError("Unhandled API exception for trace {TraceId} ({ExceptionType}) on {Method} {Path}",
                    context.TraceIdentifier, exception.GetType().Name, context.Request.Method, context.Request.Path);
            else if (status == StatusCodes.Status400BadRequest)
                logger.LogWarning("Invalid API request for trace {TraceId} ({ExceptionType}) at {Origin}",
                    context.TraceIdentifier, exception.GetType().Name, exception.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim());

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions = { ["traceId"] = context.TraceIdentifier, ["code"] = code }
            }, cancellationToken: context.RequestAborted);
        }
    }
}
