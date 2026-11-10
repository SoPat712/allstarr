using System.Net;
using System.Diagnostics;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(200, 200)]
    [InlineData(503, 200)]
    [InlineData(200, 503)]
    public async Task PassThrough_AlbumDetailOverlapsNativeListAndPreservesFallback(int listStatus, int detailStatus)
    {
        const string native = """{ "Items":[{"Id":"native-child","Name":"Oblivion","Type":"Audio","Future":1.00}], "TotalRecordCount":1 }""";
        using var backend = new ConcurrentAlbumBackend(native, listStatus, detailStatus);
        var gateway = JellyfinCatalogGateway("album", true);
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        await factory.Services.GetRequiredService<IApplicationCache>().SetStringAsync(
            "jellyfin:catalog-type:primary:listener:native-parent", "MusicAlbum", TimeSpan.FromMinutes(15));

        using var response = await client.GetAsync("/Items?parentId=native-parent&includeItemTypes=Audio&limit=200&api_key=fixture-key");

        Assert.Equal((HttpStatusCode)listStatus, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, backend.Lists);
        Assert.Equal(1, backend.Details);
        Assert.True(backend.DetailOverlappedList);
        gateway.Verify(value => value.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes Visions", 0, 10, 0, null),
            listStatus == 200 && detailStatus == 200 ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task PassThrough_AlbumFailureCancelsPendingDetail()
    {
        const string native = """{ "error":"native unavailable", "Future":1.00 }""";
        using var backend = new ConcurrentAlbumBackend(native, 503, 200, holdDetail: true);
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        await factory.Services.GetRequiredService<IApplicationCache>().SetStringAsync(
            "jellyfin:catalog-type:primary:listener:native-parent", "MusicAlbum", TimeSpan.FromMinutes(15));

        using var response = await client.GetAsync("/Items?parentId=native-parent&includeItemTypes=Audio&api_key=fixture-key")
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.True(backend.DetailCanceled);
        Assert.Equal(1, backend.Lists);
        Assert.Equal(1, backend.Details);
    }

    [Fact]
    public async Task PassThrough_FilteredAlbumListDoesNotReadParentDetail()
    {
        using var backend = new ConcurrentAlbumBackend("""{"Items":[],"TotalRecordCount":0}""", 200, 200, false);
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        await factory.Services.GetRequiredService<IApplicationCache>().SetStringAsync(
            "jellyfin:catalog-type:primary:listener:native-parent", "MusicAlbum", TimeSpan.FromMinutes(15));

        using var response = await client.GetAsync("/Items?parentId=native-parent&includeItemTypes=Audio&isFavorite=true&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, backend.Lists);
        Assert.Equal(0, backend.Details);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(503, false)]
    [InlineData(200, true)]
    public async Task PassThrough_NativePlaybackUsesExactBodiesWithoutMetadataRead(int detailStatus, bool rejectCompact)
    {
        const string id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string start = """{ "ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "PositionTicks":17, "ItemName":"Caller title", "PlaySessionId":"play-1", "IsPaused":false }""";
        const string compact = """{"ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","PositionTicks":17}""";
        const string progress = """{ "ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "PositionTicks":29, "IsPaused":false }""";
        const string stop = """{"ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","PositionTicks":31,"IsPaused":true,"Future":[1,true]}""";
        var metadataReads = 0;
        var forwarded = new List<(string Path, string Body)>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == $"/Items/{id}")
            {
                metadataReads++;
                return Json(detailStatus, """{"Name":"Backend title"}""");
            }
            Assert.StartsWith("/Sessions/Playing", path);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            forwarded.Add((path, body));
            return Json(rejectCompact && path == "/Sessions/Playing" && body == compact ? 400 : 204, "{}");
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", "listener", "playback-device");
        await manager.RegisterProxiedWebSocketAsync(key);

        using var started = await client.SendAsync(PlaybackRequest("/Sessions/Playing", start, "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, started.StatusCode);
        Assert.Equal((id, (long?)17), manager.GetLastPlayingState(key));
        using var progressed = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress", progress, "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, progressed.StatusCode);
        using var stopped = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped", stop, "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);

        Assert.Equal(0, metadataReads);
        Assert.Equal(rejectCompact ? new[] { compact, start } : new[] { compact },
            forwarded.Where(value => value.Path == "/Sessions/Playing").Select(value => value.Body));
        Assert.Equal(progress, Assert.Single(forwarded, value => value.Path.EndsWith("/Progress", StringComparison.Ordinal)).Body);
        Assert.Equal(stop.Replace("true,\"Future\"", "false,\"Future\"", StringComparison.Ordinal),
            Assert.Single(forwarded, value => value.Path.EndsWith("/Stopped", StringComparison.Ordinal)).Body);
        Assert.Null(manager.GetLastPlayingState(key).ItemId);
    }

    [Fact]
    public async Task PassThrough_ProgressTransitionKeepsReportOrderWithoutMetadataRead()
    {
        var forwarded = new List<(string Path, string Body)>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            Assert.StartsWith("/Sessions/Playing", request.RequestUri.AbsolutePath);
            forwarded.Add((request.RequestUri.AbsolutePath, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Json(204, "{}");
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", "listener", "playback-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Fixture", "Fixture", "1", new HeaderDictionary()));
        manager.UpdatePlayingItem(key, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 13);
        const string progress = """{"ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","PositionTicks":29,"IsPaused":false}""";

        using var response = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress", progress, "playback-device"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[]
        {
            ("/Sessions/Playing/Stopped", """{"ItemId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","PositionTicks":13,"IsPaused":false}"""),
            ("/Sessions/Playing", """{"ItemId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","PositionTicks":29}"""),
            ("/Sessions/Playing/Progress", progress)
        }, forwarded);
    }

    [Fact]
    public async Task PassThrough_MeasuresNativePlaybackLoggingReads()
    {
        const string id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var backend = new DelayedPassThroughBackend();
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", "listener", "playback-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Fixture", "Fixture", "1", new HeaderDictionary()));
        var proxy = factory.Services.GetRequiredService<JellyfinProxyService>();
        var cache = factory.Services.GetRequiredService<IApplicationCache>();
        var headers = new HeaderDictionary { ["X-Emby-Token"] = "fixture-key" };
        using (await client.GetAsync("/Users/Me?api_key=fixture-key")) { }
        foreach (var transition in new[] { false, true })
        {
            var route = transition ? "/Sessions/Playing/Progress" : "/Sessions/Playing/Stopped";
            var payload = $$"""{"ItemId":"{{id}}","PositionTicks":29,"IsPaused":false}""";
            string[] reports = transition ? ["/Sessions/Playing/Stopped", "/Sessions/Playing", route] : [route];
            var before = new List<double>();
            var after = new List<double>();
            var floor = new List<double>();
            for (var index = 0; index < 5; index++)
            {
                var timer = Stopwatch.StartNew();
                if (transition)
                {
                    var (oldItem, _) = await proxy.GetJsonAsync($"Items/{previous}", clientHeaders: headers);
                    oldItem?.Dispose();
                    var (stopped, _) = await proxy.PostJsonAsync(reports[0].TrimStart('/'), payload, headers);
                    stopped?.Dispose();
                }
                var (item, _) = await proxy.GetJsonAsync($"Items/{id}", clientHeaders: headers);
                item?.Dispose();
                foreach (var endpoint in reports.Skip(transition ? 1 : 0))
                {
                    var (reported, _) = await proxy.PostJsonAsync(endpoint.TrimStart('/'), payload, headers);
                    reported?.Dispose();
                }
                before.Add(timer.Elapsed.TotalMilliseconds);
                foreach (var signal in new[] { "start", "stop" })
                    foreach (var song in new[] { previous, id })
                        await cache.DeleteAsync(CacheKeyBuilder.BuildPlaybackSignalDedupeKey(
                            "primary", "listener", signal, "playback-device", song));
                manager.UpdatePlayingItem(key, transition ? previous : id, 13);
                var metadata = backend.MetadataReads;
                var count = backend.PlaybackReports;
                timer.Restart();
                using var response = await client.SendAsync(PlaybackRequest(route, payload, "playback-device"));
                after.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal(metadata, backend.MetadataReads);
                Assert.Equal(reports.Length, backend.PlaybackReports - count);
                timer.Restart();
                foreach (var endpoint in reports)
                {
                    var (reported, _) = await proxy.PostJsonAsync(endpoint.TrimStart('/'), payload, headers);
                    reported?.Dispose();
                }
                floor.Add(timer.Elapsed.TotalMilliseconds);
            }
            var measurement = $"{route} ({(transition ? "inferred transition" : "native stop")}): former lookup sequence p50={before.Order().ElementAt(2):F2} ms; current p50={after.Order().ElementAt(2):F2} ms; report-only floor p50={floor.Order().ElementAt(2):F2} ms; logging reads={(transition ? 2 : 1)} -> 0; reports={reports.Length}";
            _lyricsSearchOutput.WriteLine(measurement);
            Console.WriteLine(measurement);
        }
    }

    private sealed class ConcurrentAlbumBackend(string native, int listStatus, int detailStatus,
        bool gateList = true, bool holdDetail = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource detailStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool listComplete;
        public int Lists;
        public int Details;
        public bool DetailOverlappedList;
        public bool DetailCanceled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Items")
            {
                Interlocked.Increment(ref Lists);
                if (gateList) await detailStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                listComplete = true;
                return Json(listStatus, native);
            }
            Assert.Equal("/Users/listener/Items/native-parent", path);
            Interlocked.Increment(ref Details);
            DetailOverlappedList = !listComplete;
            detailStarted.TrySetResult();
            if (holdDetail)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { DetailCanceled = true; throw; }
            }
            return Json(detailStatus, """{"Id":"native-parent","Type":"MusicAlbum","Name":"Visions","AlbumArtist":"Grimes"}""");
        }
    }
}
