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
        var sessionService = AdminAuthSessionTestSupport.Create();
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
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
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

    [Theory]
    [InlineData("/api/admin/ui/schema", "GET")]
    [InlineData("/api/admin/provider-accounts", "GET")]
    [InlineData("/api/admin/provider-accounts", "POST")]
    [InlineData("/api/admin/provider-accounts/019f48f2-5f28-7b11-b42d-0d9b76b73b40", "DELETE")]
    [InlineData("/api/admin/provider-accounts/019f48f2-5f28-7b11-b42d-0d9b76b73b40/secret", "PUT")]
    public async Task InvokeAsync_NonAdminUser_ProviderSelfServiceRoutesPassToScopedControllers(
        string path,
        string method)
    {
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");
        var invoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);
        var context = CreateContext(path, method, 5275, session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
    }

    [Theory]
    [InlineData("/api/admin/playlist-sources", "GET")]
    [InlineData("/api/admin/playlist-sources/019f48f2-5f28-7b11-b42d-0d9b76b73b40/playlists", "GET")]
    [InlineData("/api/admin/media-targets", "GET")]
    [InlineData("/api/admin/media-targets/019f48f2-5f28-7b11-b42d-0d9b76b73b40/playlists", "GET")]
    [InlineData("/api/admin/playlist-links", "GET")]
    [InlineData("/api/admin/playlist-links", "POST")]
    [InlineData("/api/admin/playlist-links/019f48f2-5f28-7b11-b42d-0d9b76b73b40", "PUT")]
    [InlineData("/api/admin/playlist-links/019f48f2-5f28-7b11-b42d-0d9b76b73b40", "DELETE")]
    [InlineData("/api/admin/playlist-links/rematch/preview", "GET")]
    [InlineData("/api/admin/playlist-links/019f48f2-5f28-7b11-b42d-0d9b76b73b40/source-update/apply", "POST")]
    public async Task InvokeAsync_NonAdminUser_PlaylistSelfServiceRoutesPassToScopedControllers(
        string path,
        string method)
    {
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");
        var invoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);
        var context = CreateContext(path, method, 5275, session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
    }

    [Theory]
    [InlineData("/api/admin/ui/schema", "POST")]
    [InlineData("/api/admin/config", "GET")]
    [InlineData("/api/admin/status", "GET")]
    [InlineData("/api/admin/playlist-linkspoof", "GET")]
    [InlineData("/api/admin/provider-accounts", "PUT")]
    [InlineData("/api/admin/provider-accounts/not-a-guid", "DELETE")]
    [InlineData("/api/admin/provider-accounts/019f48f2-5f28-7b11-b42d-0d9b76b73b40/secret", "GET")]
    public async Task InvokeAsync_NonAdminUser_RemainsBlockedFromAdminAndInvalidAccountRoutes(
        string path,
        string method)
    {
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");
        var invoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);
        var context = CreateContext(path, method, 5275, session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/jobs", "GET")]
    [InlineData("/api/admin/jobs/019f48f2-5f28-7b11-b42d-0d9b76b73b40", "GET")]
    [InlineData("/api/admin/jobs/019f48f2-5f28-7b11-b42d-0d9b76b73b40/cancel", "POST")]
    [InlineData("/api/admin/favorite-action-policies", "GET")]
    [InlineData("/api/admin/favorite-action-policies/me", "PUT")]
    [InlineData("/api/admin/intelligence", "GET")]
    [InlineData("/api/admin/intelligence/policy", "PUT")]
    [InlineData("/api/admin/intelligence/runs", "POST")]
    [InlineData("/api/admin/intelligence/generated-sets", "POST")]
    [InlineData("/api/admin/intelligence/data", "DELETE")]
    [InlineData("/api/admin/updates/stream", "GET")]
    public async Task InvokeAsync_NonAdminUser_OwnJobRoutesPassToControllerScopeChecks(
        string path,
        string method)
    {
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "user-1",
            userName: "josh",
            isAdministrator: false,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");
        var invoked = false;
        var middleware = new AdminAuthenticationMiddleware(
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            sessionService,
            NullLogger<AdminAuthenticationMiddleware>.Instance);
        var context = CreateContext(path, method, 5275, session.SessionId);

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
    }

    [Fact]
    public async Task InvokeAsync_NonAdminUser_DisallowedRoute_Returns403()
    {
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
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
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
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
        var sessionService = AdminAuthSessionTestSupport.Create();
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
