using allstarr.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class RequestLoggingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AlwaysRedactsCredentialsAndPreservesRepeatedQueryKeys()
    {
        var messages = new List<string>();
        var logger = new CollectingLogger<RequestLoggingMiddleware>(messages);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Debug:LogAllRequests"] = "true",
                ["Debug:RedactSensitiveRequestValues"] = "false"
            })
            .Build();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("allstarr.test");
        context.Request.Path = "/rest/search3";
        context.Request.QueryString = new QueryString("?token=query-secret&id=one&id=two");
        context.Request.Headers.Authorization = "Bearer bearer-secret";
        context.Request.Headers["X-Emby-Authorization"] =
            "MediaBrowser Client=\"fixture\", Token=\"jellyfin-secret\"";
        context.Request.Headers["X-Emby-Token"] = "header-secret";

        await middleware.InvokeAsync(context);

        var log = string.Join('\n', messages);
        Assert.DoesNotContain("query-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", log, StringComparison.Ordinal);
        Assert.Contains("?token=<redacted>&id=<redacted>&id=<redacted>", log, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer ***", log, StringComparison.Ordinal);
        Assert.Contains("X-Emby-Token: ***", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotAttachExceptionThatMayContainSensitiveUrl()
    {
        var messages = new List<string>();
        var exceptions = new List<Exception>();
        var logger = new CollectingLogger<RequestLoggingMiddleware>(messages, exceptions);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Debug:LogAllRequests"] = "true"
            })
            .Build();
        var middleware = new RequestLoggingMiddleware(
            _ => throw new InvalidOperationException(
                "request https://provider.invalid/media?token=private-token failed"),
            logger,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("allstarr.test");
        context.Request.Path = "/rest/stream";
        context.Request.QueryString = new QueryString("?token=query-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        var log = string.Join('\n', messages);
        Assert.Empty(exceptions);
        Assert.DoesNotContain("private-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.invalid", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
    }

    private sealed class CollectingLogger<T>(
        List<string> messages,
        List<Exception>? exceptions = null) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception != null)
            {
                exceptions?.Add(exception);
            }
        }
    }
}
