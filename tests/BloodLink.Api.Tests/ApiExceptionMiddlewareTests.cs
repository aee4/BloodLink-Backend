using System.Text.Json;
using BloodLink.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BloodLink.Api.Tests;

public sealed class ApiExceptionMiddlewareTests
{
    [Fact]
    public async Task Unexpected_exception_returns_generic_problem_and_logs_correlatable_context()
    {
        var logger = new CaptureLogger<ApiExceptionMiddleware>();
        var middleware = new ApiExceptionMiddleware(
            _ => throw new Exception("Injected exception detail must not be disclosed."), logger);
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "test-trace-42";
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/needs/private-id";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("internal_error", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-trace-42", body.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("Injected exception detail", body.RootElement.ToString(), StringComparison.Ordinal);
        Assert.Contains(logger.Messages, message => message.Contains("test-trace-42", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("Exception", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Framework_authorization_exception_maps_to_safe_forbidden_problem()
    {
        var middleware = new ApiExceptionMiddleware(
            _ => throw new System.UnauthorizedAccessException("Internal denial reason."),
            new CaptureLogger<ApiExceptionMiddleware>());
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "forbidden-trace";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("forbidden", body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Internal denial reason", body.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "unauthenticated")]
    [InlineData(StatusCodes.Status403Forbidden, "forbidden")]
    public async Task Framework_authorization_results_use_problem_details(int status, string code)
    {
        var middleware = new ApiExceptionMiddleware(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, new CaptureLogger<ApiExceptionMiddleware>());
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "authorization-trace";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("authorization-trace", body.RootElement.GetProperty("traceId").GetString());
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
