using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using allstarr.Middleware;
using allstarr.Services.Admin;

namespace allstarr.Tests;

public class AdminAuthenticationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_UnauthenticatedAdminRequest_Returns401()
    {
        var sessionService = new AdminAuthSessionService();
        var nextInvoked = false;

        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);

        var context = CreateContext(
            path: "/api/admin/config",
            method: HttpMethods.Get,
            localPort: 5275);

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

        var body = await ReadResponseBodyAsync(context);
        Assert.Contains("Authentication required", body);
    }

    [Fact]
    public async Task InvokeAsync_NonAdminUser_AllowedRoute_PassesThrough()
    {
        var sessionService = new AdminAuthSessionService();
        var session = sessionService.CreateSession(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");

        var nextInvoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);

        var context = CreateContext(
            path: "/api/admin/jellyfin/playlists",
            method: HttpMethods.Get,
            localPort: 5275,
            sessionIdCookie: session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.True(context.Items.ContainsKey(AdminAuthSessionService.HttpContextSessionItemKey));
    }

    [Fact]
    public async Task InvokeAsync_NonAdminUser_DisallowedRoute_Returns403()
    {
        var sessionService = new AdminAuthSessionService();
        var session = sessionService.CreateSession(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");

        var nextInvoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);

        var context = CreateContext(
            path: "/api/admin/config",
            method: HttpMethods.Get,
            localPort: 5275,
            sessionIdCookie: session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var body = await ReadResponseBodyAsync(context);
        Assert.Contains("Administrator permissions required", body);
    }

    [Fact]
    public async Task InvokeAsync_AdminUser_DisallowedForUserButAllowedForAdmin_PassesThrough()
    {
        var sessionService = new AdminAuthSessionService();
        var session = sessionService.CreateSession(
            userId: "admin-1",
            userName: "admin",
            isAdministrator: true,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");

        var nextInvoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);

        var context = CreateContext(
            path: "/api/admin/config",
            method: HttpMethods.Get,
            localPort: 5275,
            sessionIdCookie: session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AdminApiOnMainPort_PassesThroughForDownstreamFilter()
    {
        var sessionService = new AdminAuthSessionService();
        var nextInvoked = false;

        var middleware = new AdminAuthenticationMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);

        var context = CreateContext(
            path: "/api/admin/config",
            method: HttpMethods.Get,
            localPort: 5274);

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string method,
        int localPort,
        string? sessionIdCookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.LocalPort = localPort;
        context.Response.Body = new MemoryStream();

        if (!string.IsNullOrWhiteSpace(sessionIdCookie))
        {
            context.Request.Headers.Cookie = $"{AdminAuthSessionService.SessionCookieName}={sessionIdCookie}";
        }

        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
