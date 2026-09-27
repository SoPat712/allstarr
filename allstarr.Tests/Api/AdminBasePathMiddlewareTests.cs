using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public class AdminBasePathMiddlewareTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/admin", "/admin")]
    [InlineData("/admin/", "/admin")]
    [InlineData("/ops_2/music-admin/", "/ops_2/music-admin")]
    public void Normalize_AcceptsSafeSegments(string? configured, string expected)
    {
        Assert.Equal(expected, AdminBasePath.Normalize(configured));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("//admin")]
    [InlineData("/admin//")]
    [InlineData("/admin/../")]
    [InlineData("/admin/%2e%2e")]
    [InlineData("/admin?return=1")]
    [InlineData("https://example.test/admin")]
    [InlineData("/admin space")]
    public void Normalize_RejectsMalformedOrExternalPrefixes(string configured)
    {
        Assert.Throws<ArgumentException>(() => AdminBasePath.Normalize(configured));
    }

    [Fact]
    public async Task InvokeAsync_PreservedPrefix_SetsPathBaseAndStripsPath()
    {
        var middleware = CreateMiddleware("/admin", out var nextPathBase, out var nextPath);
        var context = CreateContext(5275, "/admin/api/admin/ui/home");

        await middleware.InvokeAsync(context);

        Assert.Equal("/admin", nextPathBase());
        Assert.Equal("/api/admin/ui/home", nextPath());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_StrippedPrefix_RestoresPathBaseWithoutChangingPath()
    {
        var middleware = CreateMiddleware("/admin", out var nextPathBase, out var nextPath);
        var context = CreateContext(5275, "/api/admin/ui/home");

        await middleware.InvokeAsync(context);

        Assert.Equal("/admin", nextPathBase());
        Assert.Equal("/api/admin/ui/home", nextPath());
    }

    [Fact]
    public async Task InvokeAsync_ExactPrefix_RedirectsWithQueryString()
    {
        var middleware = CreateMiddleware("/admin", out var nextPathBase, out _);
        var context = CreateContext(5275, "/admin");
        context.Request.QueryString = new QueryString("?return=%2Fmusic");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
        Assert.Equal("/admin/?return=%2Fmusic", context.Response.Headers.Location.ToString());
        Assert.True(string.IsNullOrEmpty(nextPathBase()));
    }

    [Fact]
    public async Task InvokeAsync_NonAdminPort_DoesNotSetPathBase()
    {
        var middleware = CreateMiddleware("/admin", out var nextPathBase, out var nextPath);
        var context = CreateContext(8080, "/admin/api/admin/ui/home");

        await middleware.InvokeAsync(context);

        Assert.True(string.IsNullOrEmpty(nextPathBase()));
        Assert.Equal("/admin/api/admin/ui/home", nextPath());
    }

    private static AdminBasePathMiddleware CreateMiddleware(
        string configured,
        out Func<string?> nextPathBase,
        out Func<string?> nextPath)
    {
        string? observedPathBase = null;
        string? observedPath = null;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminBasePath.ConfigurationKey] = configured,
            })
            .Build();
        var basePath = new AdminBasePath(configuration);

        nextPathBase = () => observedPathBase;
        nextPath = () => observedPath;
        return new AdminBasePathMiddleware(context =>
        {
            observedPathBase = context.Request.PathBase.Value;
            observedPath = context.Request.Path.Value;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, basePath);
    }

    private static DefaultHttpContext CreateContext(int localPort, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
