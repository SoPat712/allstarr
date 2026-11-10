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

    [Fact]
    public async Task NestedAndRepeatedStagesSumAcrossAsyncBoundaries()
    {
        using var timing = RequestTiming.Begin();
        double inner;
        using (RequestTiming.Current?.Stage("build"))
        {
            await Task.Yield();
            Assert.Same(timing, RequestTiming.Current);
            using (RequestTiming.Current?.Stage("build"))
                await Task.Delay(10);
            inner = timing.Snapshot("stats").Stages["build"];
        }
        var nested = timing.Snapshot("stats").Stages["build"];
        Assert.True(inner > 0);
        Assert.True(nested >= inner * 2);
        using (RequestTiming.Current?.Stage("build"))
            await Task.Delay(10);
        Assert.True(timing.Snapshot("stats").Stages["build"] > nested);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using (RequestTiming.Current?.Stage("render"))
                await Task.Delay(10);
        }));
        var result = timing.Snapshot("stats");
        Assert.Equal(2, result.Stages.Count);
        Assert.True(result.Stages["render"] >= 40);
    }

    [Fact]
    public void NamedStagesRejectPrivateValuesAndStopAtEightDistinctNames()
    {
        using var timing = RequestTiming.Begin();
        string[] rejected = ["private-user-id", "https://private.test/path?token=private", "build;desc=\"private\"", "Build", ""];
        foreach (var name in rejected)
            using (timing.Stage(name)) { }
        using (timing.Stage(null!)) { }
        string[] accepted = ["lock", "build", "render", "match", "merge", "serialize", "queue", "library"];
        foreach (var name in accepted)
            using (timing.Stage(name)) { }
        string[] remaining = ["sources", "saved", "owned", "resolve", "fetch", "store"];
        foreach (var name in remaining)
            using (timing.Stage(name)) { }
        using (timing.Stage("lock")) { }

        var result = timing.Snapshot("stats");
        Assert.Equal(accepted.Order(), result.Stages.Keys.Order());
        Assert.Equal(16, result.Header.Split(", ").Length);
        Assert.DoesNotContain("private", result.Header);
        Assert.DoesNotContain("sources;", result.Header);
        Assert.DoesNotContain("saved;", result.Header);
        Assert.DoesNotContain("owned;", result.Header);
        Assert.DoesNotContain("resolve;", result.Header);
        Assert.DoesNotContain("fetch;", result.Header);
        Assert.DoesNotContain("store;", result.Header);

        using var other = RequestTiming.Begin();
        foreach (var name in remaining)
            using (other.Stage(name)) { }
        Assert.Equal(remaining.Order(), other.Snapshot("stats").Stages.Keys.Order());
    }

    [Fact]
    public void DisabledStageCallsAllocateNothingAfterWarmup()
    {
        Assert.Null(RequestTiming.Current);
        for (var i = 0; i < 512; i++)
            using (RequestTiming.Current?.Stage("lock")) { }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            using (RequestTiming.Current?.Stage("lock")) { }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
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
        Assert.Contains($"route;desc=\"{route}\", pre;dur=", header);
        Assert.Matches(", inflight;desc=\"[0-9]+\", build;dur=", header);
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
        using var timing = RequestTiming.Begin(12.3, 4);
        using var client = new HttpClient(new RequestTimingHandler(true) { InnerHandler = new Failure() });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://backend.invalid/private?token=secret"));
        Assert.Equal(1, timing.Snapshot("jellyfin").JellyfinCalls);
        timing.Add(RequestTimingStage.Db, TimeSpan.FromMilliseconds(1.2));
        using (timing.Stage("serialize")) await Task.Delay(10);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = timing.Snapshot("jellyfin");
            Assert.Contains("db;dur=1.2", result.Header);
            Assert.Equal(12.3, result.PreMs);
            Assert.Equal(4, result.Inflight);
            Assert.Contains(FormattableString.Invariant($"route;desc=\"jellyfin\", pre;dur=12.3, inflight;desc=\"4\", serialize;dur={result.Stages["serialize"]:0.0}"), result.Header);
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
            using (RequestTiming.Current?.Stage("build")) await Task.Yield();
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
