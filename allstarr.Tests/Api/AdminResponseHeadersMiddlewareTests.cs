using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace allstarr.Tests;

public sealed class AdminResponseHeadersMiddlewareTests
{
    [Theory]
    [InlineData("GET", "/", 200)]
    [InlineData("HEAD", "/", 200)]
    [InlineData("GET", "/_app/fixture.js", 200)]
    [InlineData("HEAD", "/_app/fixture.js", 200)]
    [InlineData("GET", "/api/admin/denied", 401)]
    [InlineData("GET", "/api/admin/forbidden", 403)]
    [InlineData("GET", "/api/admin/missing", 404)]
    [InlineData("GET", "/api/admin/error", 500)]
    [InlineData("GET", "/.env", 404)]
    public async Task AdminResponsesRetainTheirContentsAndProtectTheBrowser(string method, string path, int status)
    {
        using var fixture = new Fixture();
        using var before = await fixture.StartAsync(false);
        using var after = await fixture.StartAsync(true);
        using var original = await SendAsync(before, method, path);
        using var response = await SendAsync(after, method, path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(original.StatusCode, response.StatusCode);
        Assert.Equal(await original.Content.ReadAsByteArrayAsync(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(original.Content.Headers.ContentType, response.Content.Headers.ContentType);
        Assert.Equal(original.Headers.CacheControl, response.Headers.CacheControl);
        AssertProtection(response);
    }

    [Fact]
    public async Task PrefixTransformationKeepsItsExistingScriptHashPolicyAndBytes()
    {
        using var fixture = new Fixture();
        using var before = await fixture.StartAsync(false, prefix: "/console");
        using var after = await fixture.StartAsync(true, prefix: "/console");
        using var original = await SendAsync(before, "GET", "/console/");
        using var response = await SendAsync(after, "GET", "/console/");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(await original.Content.ReadAsStringAsync(), body);
        Assert.Contains("import(\"/console/_app/fixture.js\")", body);
        Assert.Contains("script-src 'self' 'sha256-", body);
        Assert.DoesNotContain("unsafe-inline", body);
        AssertProtection(response);
    }

    [Fact]
    public async Task PrefixRedirectIsProtectedBeforeStaticFilesOrAuthentication()
    {
        using var fixture = new Fixture();
        using var host = await fixture.StartAsync(true, prefix: "/console");
        using var response = await SendAsync(host, "GET", "/console?fixture=1");
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("/console/?fixture=1", response.Headers.Location!.OriginalString);
        AssertProtection(response);
    }

    [Fact]
    public async Task UntrustedNetworkDenialIsProtectedBeforeLaterMiddleware()
    {
        using var fixture = new Fixture();
        using var before = await fixture.StartAsync(false, remote: "198.51.100.4");
        using var after = await fixture.StartAsync(true, remote: "198.51.100.4");
        using var original = await SendAsync(before, "GET", "/api/admin/private");
        using var response = await SendAsync(after, "GET", "/api/admin/private");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(await original.Content.ReadAsByteArrayAsync(), await response.Content.ReadAsByteArrayAsync());
        AssertProtection(response);
    }

    [Fact]
    public async Task LaterCallbacksCannotWeakenFramingAndKeepTheExistingContentPolicy()
    {
        using var fixture = new Fixture();
        using var host = await fixture.StartAsync(true);
        using var response = await SendAsync(host, "GET", "/api/admin/existing");
        AssertProtection(response);
        var policies = response.Headers.GetValues("Content-Security-Policy").ToArray();
        Assert.Contains("default-src 'self'; script-src 'nonce-fixture'", policies);
        Assert.Equal(2, policies.Length);
    }

    [Theory]
    [InlineData(5274)]
    [InlineData(8080)]
    [InlineData(80)]
    public async Task OtherListenersKeepEveryNativeResponseByteAndHeader(int port)
    {
        using var fixture = new Fixture();
        using var before = await fixture.StartAsync(false, port);
        using var after = await fixture.StartAsync(true, port);
        using var original = await SendAsync(before, "GET", "/Items/fixture");
        using var response = await SendAsync(after, "GET", "/Items/fixture");
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(await original.Content.ReadAsByteArrayAsync(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(original.Content.Headers.ToString(), response.Content.Headers.ToString());
        Assert.Equal(original.Headers.ToString(), response.Headers.ToString());
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string method, string path)
    {
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        return await client.SendAsync(request);
    }

    private static void AssertProtection(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var sniff));
        Assert.Equal("nosniff", Assert.Single(sniff));
        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frames));
        Assert.Equal("DENY", Assert.Single(frames));
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var policies));
        Assert.Contains("frame-ancestors 'none'", policies);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-admin-browser-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(root, "_app"));
            const string bootstrap = "import(\"/_app/fixture.js\")";
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(bootstrap)));
            File.WriteAllText(Path.Combine(root, "index.html"), $"""
                <meta name="allstarr-base-path" content="" />
                <meta http-equiv="content-security-policy" content="default-src 'self'; script-src 'self' 'sha256-{hash}';">
                <script>{bootstrap}</script>
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "_app", "fixture.js"), "window.fixture = true;", new UTF8Encoding(false));
        }

        internal Task<IHost> StartAsync(bool protect, int port = 5275, string prefix = "", string remote = "127.0.0.1") =>
            new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseContentRoot(root).UseWebRoot(root)
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Admin:BasePath"] = prefix,
                        ["Admin:Containerized"] = "false",
                        ["Admin:TrustedSubnets"] = "127.0.0.1/32"
                    }).Build());
                    services.AddSingleton<AdminBasePath>();
                }).Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.Connection.LocalPort = port;
                        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                        return next(context);
                    });
                    if (protect) app.UseMiddleware<AdminResponseHeadersMiddleware>();
                    app.UseMiddleware<BotProbeBlockMiddleware>();
                    app.UseExceptionHandler(handler => handler.Run(async context =>
                    {
                        context.Response.Clear();
                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("Unavailable");
                    }));
                    app.UseMiddleware<AdminNetworkAllowlistMiddleware>();
                    app.UseMiddleware<AdminBasePathMiddleware>();
                    app.UseMiddleware<AdminStaticFilesMiddleware>();
                    app.Run(async context =>
                    {
                        if (context.Connection.LocalPort != 5275)
                        {
                            context.Response.StatusCode = 206;
                            context.Response.ContentType = "application/vnd.fixture+json";
                            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
                            await context.Response.WriteAsync("{\"Id\":\"native\",\"Number\":1.00}");
                            return;
                        }
                        var path = context.Request.Path.Value;
                        if (path == "/api/admin/error") throw new InvalidOperationException("Fixture failure");
                        context.Response.StatusCode = path == "/api/admin/denied" ? 401 : path == "/api/admin/forbidden" ? 403 : path == "/api/admin/missing" ? 404 : 200;
                        context.Response.ContentType = "application/json";
                        if (path == "/api/admin/existing") context.Response.OnStarting(() =>
                        {
                            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                            context.Response.Headers["X-Content-Type-Options"] = "off";
                            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'nonce-fixture'";
                            return Task.CompletedTask;
                        });
                        await context.Response.WriteAsync("{\"status\":\"fixture\"}");
                    });
                })).StartAsync();

        public void Dispose() => Directory.Delete(root, true);
    }
}
