using System.Globalization;
using allstarr.Core.Operations;
using allstarr.Middleware;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class RequestTimingTests
{
    [Fact]
    public async Task AccumulatorFlowsAcrossAsyncBoundariesAndRestoresItsParent()
    {
        Assert.Null(RequestTiming.Current);
        using (var parent = RequestTiming.Begin())
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await Task.Yield();
                Assert.Same(parent, RequestTiming.Current);
                RequestTiming.Current!.Add(RequestTimingStage.Jellyfin, TimeSpan.FromMilliseconds(2));
                RequestTiming.Current.NoteCache(true);
            }));
            using (var child = RequestTiming.Begin())
            {
                child.Add(RequestTimingStage.Provider, TimeSpan.FromMilliseconds(3));
                Assert.Equal(3, child.Snapshot("other").ProviderMs);
            }
            Assert.Same(parent, RequestTiming.Current);
            parent.Add(RequestTimingStage.Db, TimeSpan.FromMilliseconds(1));
            parent.NoteCache(false);
            var result = parent.Snapshot("other");
            Assert.Equal(16, result.JellyfinMs);
            Assert.Equal(8, result.JellyfinCalls);
            Assert.Equal(1, result.DbMs);
            Assert.Equal(0, result.ProviderMs);
            Assert.Equal("hit/miss", result.Cache);
        }
        Assert.Null(RequestTiming.Current);
    }

    [Theory]
    [InlineData("/allstarr/v1/me/sound", 8080, "sound")]
    [InlineData("/Users/private-id/Items", 8080, "jellyfin")]
    [InlineData("/rest/getAlbum.view", 8080, "subsonic")]
    [InlineData("/api/admin/settings", 5275, "admin")]
    [InlineData("/", 5275, "admin")]
    public async Task HeaderContainsOnlyFixedLabelsAndStageMeasurements(string path, int port, string route)
    {
        await using var server = await Server(true, port);
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path + "?api_key=private-secret&id=private-id");
        request.Headers.Add("X-Emby-Authorization", "MediaBrowser Client=\"Aloud\", Token=\"private-secret\"");
        using var response = await client.SendAsync(request);
        var header = Assert.Single(response.Headers.GetValues("Server-Timing"));
        Assert.Contains("total;dur=", header);
        Assert.Contains("jellyfin;dur=8.1;desc=\"2 calls\"", header);
        Assert.Contains("db;dur=1.2", header);
        Assert.Contains("provider;dur=0", header);
        Assert.Contains("cache;desc=\"hit\"", header);
        Assert.Contains($"route;desc=\"{route}\"", header);
        Assert.DoesNotContain("private", header);
        Assert.DoesNotContain("Aloud", header);
    }

    [Fact]
    public async Task SettingDisablesHeaderAndAccumulator()
    {
        await using var server = await Server(false, 8080);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync("/allstarr/v1/me/stats");
        Assert.False(response.Headers.Contains("Server-Timing"));
    }

    [Fact]
    public async Task HeaderIsEnabledWhenSettingIsAbsent()
    {
        await using var server = await Server(null, 8080);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync("/System/Info/Public");
        Assert.True(response.Headers.Contains("Server-Timing"));
    }

    [Fact]
    public async Task OutboundTimingCountsFailedCallsAndUsesInvariantNumbers()
    {
        using var timing = RequestTiming.Begin();
        using var client = new HttpClient(new RequestTimingHandler(true) { InnerHandler = new Failure() });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://backend.invalid/private?token=secret"));
        Assert.Equal(1, timing.Snapshot("jellyfin").JellyfinCalls);
        timing.Add(RequestTimingStage.Db, TimeSpan.FromMilliseconds(1.2));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Contains("db;dur=1.2", timing.Snapshot("jellyfin").Header);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task DatabaseTimingMeasuresSyncAndAsyncCommandsWithoutTheirText()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(new RequestTimingDbInterceptor()).Options;
        await using var db = new AllstarrDbContext(options);
        using var timing = RequestTiming.Begin();
        _ = db.Users.Count();
        var sync = timing.Snapshot("stats").DbMs;
        Assert.True(sync > 0);
        _ = await db.Users.CountAsync();
        Assert.True(timing.Snapshot("stats").DbMs > sync);
        Assert.DoesNotContain("users", timing.Snapshot("stats").Header);
    }

    [Fact]
    public void SynchronousProviderCallsAreMeasured()
    {
        using var timing = RequestTiming.Begin();
        using var client = new HttpClient(new RequestTimingHandler(false) { InnerHandler = new Failure() });
        Assert.Throws<HttpRequestException>(() => client.Send(new HttpRequestMessage(HttpMethod.Get, "https://provider.invalid")));
        Assert.True(timing.Snapshot("other").ProviderMs > 0);
    }

    private static async Task<WebApplication> Server(bool? enabled, int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (enabled.HasValue) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Diagnostics:ServerTimingEnabled"] = enabled.Value.ToString() });
        var app = builder.Build();
        app.Use(async (http, next) => { http.Connection.LocalPort = port; await next(http); });
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.Run(async http =>
        {
            Assert.Equal(enabled ?? true, RequestTiming.Current != null);
            RequestTiming.Current?.Add(RequestTimingStage.Jellyfin, TimeSpan.FromMilliseconds(4));
            await Task.Yield();
            RequestTiming.Current?.Add(RequestTimingStage.Jellyfin, TimeSpan.FromMilliseconds(4.1));
            RequestTiming.Current?.Add(RequestTimingStage.Db, TimeSpan.FromMilliseconds(1.2));
            RequestTiming.Current?.NoteCache(true);
            await http.Response.WriteAsync("ok");
        });
        await app.StartAsync();
        return app;
    }

    private sealed class Failure : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Unavailable");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Unavailable");
    }
}
