using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using allstarr.Middleware;

namespace allstarr.Tests;

public class AdminNetworkAllowlistMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AdminPortLoopback_AllowsRequest()
    {
        var middleware = CreateMiddleware(new Dictionary<string, string?>(), out var nextInvoked);
        var context = CreateContext(5275, "127.0.0.1");

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AdminPortUntrustedSubnet_BlocksRequest()
    {
        var middleware = CreateMiddleware(new Dictionary<string, string?>(), out var nextInvoked);
        var context = CreateContext(5275, "192.168.1.25");

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AdminPortTrustedSubnet_AllowsRequest()
    {
        var middleware = CreateMiddleware(new Dictionary<string, string?>
        {
            ["Admin:TrustedSubnets"] = "192.168.1.0/24"
        }, out var nextInvoked);
        var context = CreateContext(5275, "192.168.1.25");

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_NonAdminPort_BypassesAllowlist()
    {
        var middleware = CreateMiddleware(new Dictionary<string, string?>(), out var nextInvoked);
        var context = CreateContext(8080, "8.8.8.8");

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    private static AdminNetworkAllowlistMiddleware CreateMiddleware(
        IDictionary<string, string?> configValues,
        out Func<bool> nextInvoked)
    {
        var invoked = false;
        nextInvoked = () => invoked;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new AdminNetworkAllowlistMiddleware(
            context =>
            {
                invoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            configuration,
            NullLogger<AdminNetworkAllowlistMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(int localPort, string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Path = "/api/admin/status";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
