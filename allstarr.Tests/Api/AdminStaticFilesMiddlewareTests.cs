using System.Security.Cryptography;
using System.Text;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    public void PrefixTransformationDoesNotAuthorizePreviouslyBlockedInlineScripts()
    {
        const string html = "<meta http-equiv=\"content-security-policy\" content=\"script-src 'self'\"><script>import(\"/_app/app.js\")</script>";
        var transformed = AdminStaticFilesMiddleware.TransformIndexHtml(html, "/admin");
        Assert.DoesNotContain("sha256-", transformed);
        Assert.Contains("import(\"/admin/_app/app.js\")", transformed);
    }

    [Fact]
    public async Task InvokeAsync_ConfiguredPrefix_TransformsIndexAssetsAndCsp()
    {
        const string bootstrap = "import(\"/_app/immutable/entry/app.js\")";
        var oldHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(bootstrap)));
        var html = $"""
            <meta name="allstarr-base-path" content="" />
            <meta http-equiv="content-security-policy" content="default-src 'self'; script-src 'self' 'sha256-{oldHash}';">
            <script>{bootstrap}</script>
            <link rel="modulepreload" href="/_app/immutable/entry/start.js">
            <link rel="icon" href="/favicon.svg">
            """;
        var webRoot = CreateTempWebRoot();
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), html);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AdminBasePath.ConfigurationKey] = "/admin/",
                })
                .Build();
            var basePath = new AdminBasePath(configuration);
            var middleware = CreateMiddleware(webRoot, out var nextInvoked, basePath);
            var context = CreateContext(localPort: 5275, path: "/");

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
            var transformed = await reader.ReadToEndAsync();
            var newHash = Convert.ToBase64String(SHA256.HashData(
                Encoding.UTF8.GetBytes("import(\"/admin/_app/immutable/entry/app.js\")")));

            Assert.False(nextInvoked());
            Assert.Contains("name=\"allstarr-base-path\" content=\"/admin\"", transformed);
            Assert.Contains("href=\"/admin/_app/immutable/entry/start.js\"", transformed);
            Assert.Contains("href=\"/admin/favicon.svg\"", transformed);
            Assert.Contains($"'sha256-{newHash}'", transformed);
            Assert.DoesNotContain($"'sha256-{oldHash}'", transformed);
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
    public async Task InvokeAsync_HashedAsset_ServesWithImmutableCaching()
    {
        var webRoot = CreateTempWebRoot();
        var assetDir = Path.Combine(webRoot, "_app", "immutable", "chunks");
        Directory.CreateDirectory(assetDir);
        await File.WriteAllTextAsync(Path.Combine(assetDir, "app.abc123.js"), "export {};");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(
                localPort: 5275,
                path: "/_app/immutable/chunks/app.abc123.js");

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
    public async Task InvokeAsync_GeneratedCss_RevalidatesAcrossDeployments()
    {
        var webRoot = CreateTempWebRoot();
        var assetDir = Path.Combine(webRoot, "_app", "immutable", "assets");
        Directory.CreateDirectory(assetDir);
        await File.WriteAllTextAsync(Path.Combine(assetDir, "app.abc123.css"), ".status-pill{}");

        try
        {
            var middleware = CreateMiddleware(webRoot, out var nextInvoked);
            var context = CreateContext(
                localPort: 5275,
                path: "/_app/immutable/assets/app.abc123.css");

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked());
            Assert.Equal("text/css", context.Response.ContentType);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
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
        out Func<bool> nextInvoked,
        AdminBasePath? basePath = null)
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
            environment.Object,
            basePath);
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
