using allstarr.Middleware;
using Microsoft.AspNetCore.Http;
using Moq;

namespace allstarr.Tests;

public class AdminStaticFilesMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AdminRootPath_ServesIndexHtml()
    {
        var webRoot = CreateTempWebRoot();
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html>ok</html>");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 5275, path: "/");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal("text/html", context.Response.ContentType);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_AdminPathTraversalAttempt_ReturnsNotFound()
    {
        var webRoot = CreateTempWebRoot();
        var parent = Directory.GetParent(webRoot)!.FullName;
        await File.WriteAllTextAsync(Path.Combine(parent, "secret.txt"), "secret");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 5275, path: "/../secret.txt");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_AdminValidStaticFile_ServesFile()
    {
        var webRoot = CreateTempWebRoot();
        var jsDir = Path.Combine(webRoot, "js");
        Directory.CreateDirectory(jsDir);
        await File.WriteAllTextAsync(Path.Combine(jsDir, "app.js"), "console.log('ok');");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 5275, path: "/js/app.js");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal("application/javascript", context.Response.ContentType);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
            Assert.Equal("no-cache", context.Response.Headers.Pragma);
            Assert.Equal("0", context.Response.Headers.Expires);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_NextRoot_ServesUncachedSvelteEntry()
    {
        var webRoot = CreateTempWebRoot();
        var nextDir = Path.Combine(webRoot, "next");
        Directory.CreateDirectory(nextDir);
        await File.WriteAllTextAsync(Path.Combine(nextDir, "index.html"), "<html>svelte</html>");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 5275, path: "/next/");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal("text/html", context.Response.ContentType);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_NextWithoutTrailingSlash_RedirectsToCanonicalEntry()
    {
        var webRoot = CreateTempWebRoot();

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 5275, path: "/next");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
            Assert.Equal("/next/", context.Response.Headers.Location);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_NextHashedAsset_ServesWithImmutableCaching()
    {
        var webRoot = CreateTempWebRoot();
        var assetDir = Path.Combine(webRoot, "next", "_app", "immutable", "chunks");
        Directory.CreateDirectory(assetDir);
        await File.WriteAllTextAsync(Path.Combine(assetDir, "app.abc123.js"), "export {};");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(
                localPort: 5275,
                path: "/next/_app/immutable/chunks/app.abc123.js");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal("application/javascript", context.Response.ContentType);
            Assert.Equal(
                "public, max-age=31536000, immutable",
                context.Response.Headers.CacheControl);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    [Fact]
    public async Task InvokeAsync_NonAdminPort_BypassesStaticMiddleware()
    {
        var webRoot = CreateTempWebRoot();
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html>ok</html>");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(localPort: 8080, path: "/index.html");

            await middleware.InvokeAsync(context);

            Assert.True(nextInvoked());
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }
        finally
        {
            DeleteTempWebRoot(webRoot);
        }
    }

    private static AdminStaticFilesMiddleware CreateMiddleware(
        string webRootPath,
        out Func<bool> nextInvoked)
    {
        var invoked = false;
        nextInvoked = () => invoked;

        var environment = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        environment.SetupGet(x => x.WebRootPath).Returns(webRootPath);

        return new AdminStaticFilesMiddleware(
            context =>
            {
                invoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            environment.Object);
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

    private static string CreateTempWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempWebRoot(string webRoot)
    {
        var testRoot = Directory.GetParent(webRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(testRoot) && Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
