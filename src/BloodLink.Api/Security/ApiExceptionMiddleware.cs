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
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, title) = exception switch
            {
                EntityNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
                BloodLink.Domain.Exceptions.UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
                ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
                ConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency conflict"),
                InsufficientInventoryException or InvalidFacilityStatusException or BusinessRuleViolationException
                    => (StatusCodes.Status409Conflict, "Request conflicts with current state"),
                InvalidOperationException => (StatusCodes.Status409Conflict, "Request conflicts with current state"),
                _ => (StatusCodes.Status500InternalServerError, "Request could not be completed")
            };

            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError("Unhandled API exception for trace {TraceId} ({ExceptionType})", context.TraceIdentifier, exception.GetType().Name);
            else if (status == StatusCodes.Status400BadRequest)
                logger.LogWarning("Invalid API request for trace {TraceId} ({ExceptionType}) at {Origin}",
                    context.TraceIdentifier, exception.GetType().Name, exception.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim());

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = title,
                Instance = context.Request.Path,
                Extensions = { ["traceId"] = context.TraceIdentifier }
            });
        }
    }
}
