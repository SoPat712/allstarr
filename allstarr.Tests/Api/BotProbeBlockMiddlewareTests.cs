using System.Net;
using allstarr.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public class BotProbeBlockMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ScannerPath_Returns404WithoutCallingNext()
    {
        var nextInvoked = false;
        var middleware = new BotProbeBlockMiddleware(
            _ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            },
            NullLogger<BotProbeBlockMiddleware>.Instance);

        var context = CreateContext("/.env");

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_NormalPath_CallsNext()
    {
        var nextInvoked = false;
        var middleware = new BotProbeBlockMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            NullLogger<BotProbeBlockMiddleware>.Instance);

        var context = CreateContext("/System/Info/Public");

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Response.Body = new MemoryStream();
        return context;
    }
}
