using System.Diagnostics;
using System.Net;
using System.Text;
using allstarr.Core.Storage;
using System.Text.Json;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinProxy_MeasuresUncachedAndWarmVerificationAgainstDelayedBackend()
    {
        var measurements = Path.Combine(AppContext.BaseDirectory, "pass-through-auth.txt");
        File.WriteAllText(measurements, "");
        using var backend = new DelayedPassThroughBackend();
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        client.DefaultRequestHeaders.Add("X-Emby-Authorization",
            "MediaBrowser Client=\"Aloud\", Device=\"Phone\", DeviceId=\"measurement-phone\", Token=\"measurement-token\"");
        var proxy = factory.Services.GetRequiredService<JellyfinProxyService>();
        await factory.Services.GetRequiredService<JellyfinSessionManager>().RegisterProxiedWebSocketAsync(
            new JellyfinSessionKey("primary", "listener", "measurement-phone"));
        var headers = new HeaderDictionary { ["X-Emby-Token"] = "measurement-token" };
        string[] routes = ["/Users/Me", "/Items/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?userId=listener", "/Items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "/Items?parentId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&includeItemTypes=Audio&limit=200", "/Artists/AlbumArtists?limit=200",
            "/Items/Latest?userId=listener", "/Items?includeItemTypes=Audio&limit=200&sortBy=SortName",
            "/Sessions/Playing", "/Items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/Images/Primary?fillWidth=600&quality=90&tag=art",
            "/Audio/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/stream?static=true"];
        foreach (var route in routes)
        {
            async Task<double> Send()
            {
                if (route == "/Sessions/Playing")
                    await factory.Services.GetRequiredService<IApplicationCache>().DeleteAsync(
                        CacheKeyBuilder.BuildPlaybackSignalDedupeKey("primary", "listener", "start",
                            "measurement-phone", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
                var timer = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(route == "/Sessions/Playing" ? HttpMethod.Post : HttpMethod.Get, route);
                if (request.Method == HttpMethod.Post)
                    request.Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        ItemId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        PositionTicks = 0L,
                        PlaySessionId = Guid.NewGuid().ToString("N")
                    }), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, route.StartsWith("/Audio/", StringComparison.Ordinal)
                    ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
                Assert.True(response.IsSuccessStatusCode, route + ": " + response.StatusCode);
                if (route.StartsWith("/Audio/", StringComparison.Ordinal))
                {
                    await using var audio = await response.Content.ReadAsStreamAsync();
                    Assert.Equal(1, await audio.ReadAsync(new byte[1]));
                }
                return timer.Elapsed.TotalMilliseconds;
            }
            await Send();
            var before = new List<double>();
            var after = new List<double>();
            var initialVerifications = backend.Verifications;
            var initialReports = backend.PlaybackReports;
            for (var index = 0; index < 5; index++)
            {
                proxy.Verifications.Evict(headers, null);
                before.Add(await Send());
            }
            Assert.Equal(5, backend.Verifications - initialVerifications);
            if (route == "/Sessions/Playing") Assert.Equal(5, backend.PlaybackReports - initialReports);
            initialReports = backend.PlaybackReports;
            initialVerifications = backend.Verifications;
            for (var index = 0; index < 5; index++) after.Add(await Send());
            Assert.Equal(initialVerifications, backend.Verifications);
            if (route == "/Sessions/Playing") Assert.Equal(5, backend.PlaybackReports - initialReports);
            var direct = new List<double>();
            using var backendClient = new HttpClient(backend, disposeHandler: false);
            for (var index = 0; index < 5; index++)
            {
                var timer = Stopwatch.StartNew();
                using var directRequest = new HttpRequestMessage(route == "/Sessions/Playing" ? HttpMethod.Post : HttpMethod.Get,
                    "http://backend.test" + route);
                using var directResponse = await backendClient.SendAsync(directRequest, route.StartsWith("/Audio/", StringComparison.Ordinal)
                    ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
                if (route.StartsWith("/Audio/", StringComparison.Ordinal))
                {
                    await using var audio = await directResponse.Content.ReadAsStreamAsync();
                    Assert.Equal(1, await audio.ReadAsync(new byte[1]));
                }
                direct.Add(timer.Elapsed.TotalMilliseconds);
            }
            var measurement = $"{route}: uncached p50={before.Order().ElementAt(2):F2} ms; warm p50={after.Order().ElementAt(2):F2} ms; direct p50={direct.Order().ElementAt(2):F2} ms; verification calls=5 -> 0";
            _lyricsSearchOutput.WriteLine(measurement);
            File.AppendAllText(measurements, measurement + Environment.NewLine);
        }
    }

    [Fact]
    public async Task JellyfinProxy_MeasuresArtistBodyBufferingBeforeFirstBytes()
    {
        const string prefix = "{ \"Items\": [";
        const string suffix = "{\"Id\":\"artist\",\"Type\":\"MusicArtist\",\"Name\":\"B & <B>\"}], \"TotalRecordCount\":1 }";
        using var backend = new DelayedArtistBodyBackend(prefix, suffix);
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(backend));
        });
        var proxy = factory.Services.GetRequiredService<JellyfinProxyService>();
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        var buffered = new List<double>();
        var streamed = new List<double>();
        for (var index = 0; index < 7; index++)
        {
            var timer = Stopwatch.StartNew();
            var (json, status) = await proxy.GetJsonAsync("Artists/AlbumArtists", clientHeaders: request.Headers);
            using (json)
            {
                Assert.Equal(200, status);
                Assert.NotNull(json);
                if (index > 0) buffered.Add(timer.Elapsed.TotalMilliseconds);
            }
            timer.Restart();
            using var response = await proxy.SendPassthroughResponseAsync(request, "Artists/AlbumArtists");
            await using var body = await response.Content.ReadAsStreamAsync();
            var first = new byte[Encoding.UTF8.GetByteCount(prefix)];
            await body.ReadExactlyAsync(first);
            if (index > 0) streamed.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(prefix, Encoding.UTF8.GetString(first));
            using var tail = new StreamReader(body);
            Assert.Equal(suffix, await tail.ReadToEndAsync());
        }
        var measurement = $"Artist body TTFB: buffered p50={buffered.Order().ElementAt(3):F2} ms; streamed p50={streamed.Order().ElementAt(3):F2} ms; tail delay=25 ms; exact bytes retained.";
        _lyricsSearchOutput.WriteLine(measurement);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pass-through-body.txt"), measurement + Environment.NewLine);
    }

    private sealed class DelayedPassThroughBackend : HttpMessageHandler
    {
        public int Verifications;
        public int PlaybackReports;
        public static int Delay(string route) => route.Split('?')[0] switch
        {
            "/Users/Me" => 150,
            "/Artists/AlbumArtists" => 134,
            "/Items/Latest" => 156,
            "/Items" => 62,
            "/Sessions/Playing" => 45,
            "/Audio/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/stream" => 20,
            _ => 40
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me" && request.Headers.Accept.Count > 0) Interlocked.Increment(ref Verifications);
            await Task.Delay(Delay(path), cancellationToken);
            if (path == "/Sessions/Playing")
            {
                Interlocked.Increment(ref PlaybackReports);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.Contains("/Images/", StringComparison.Ordinal) || path.StartsWith("/Audio/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            if (path == "/Users/Me") return Json(200, """{"Id":"listener","Name":"Listener"}""");
            if (path.Contains("/Items/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal)) return Json(200, """{"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Type":"MusicAlbum","Name":"Album"}""");
            if (path.Contains("/Items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal)) return Json(200, """{"Id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","Type":"Audio","Name":"Song"}""");
            var album = request.RequestUri.Query.Contains("ids=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.OrdinalIgnoreCase);
            var id = album ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            if (request.RequestUri.Query.Contains("ids=", StringComparison.OrdinalIgnoreCase))
                return Json(200, JsonSerializer.Serialize(new { Items = new[] { new { Id = id, Type = album ? "MusicAlbum" : "Audio", Name = "Native" } }, TotalRecordCount = 1, StartIndex = 0 }));
            var artist = path.StartsWith("/Artists", StringComparison.Ordinal);
            var rows = Enumerable.Range(0, path == "/Items/Latest" ? 10 : 200).Select(index => new
            {
                Id = index.ToString("x32"),
                Type = artist ? "MusicArtist" : "Audio",
                Name = "Native " + index,
                Artists = new[] { "Artist" },
                Album = "Album",
                RunTimeTicks = 1800000000L,
                MediaSources = new[] { new { Id = index.ToString("x32"), Container = "flac", Bitrate = 900000 } },
                UnknownFutureField = new string('x', 256)
            }).ToArray();
            return Json(200, path == "/Items/Latest" ? JsonSerializer.Serialize(rows) :
                JsonSerializer.Serialize(new { Items = rows, TotalRecordCount = rows.Length, StartIndex = 0 }));
        }
    }

    private sealed class DelayedArtistBodyBackend(string prefix, string suffix) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedArtistBody(prefix, suffix))
            });
    }

    private sealed class DelayedArtistBody(string prefix, string suffix) : MemoryStream(Encoding.UTF8.GetBytes(prefix + suffix))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length) return 0;
            if (Position >= prefix.Length) await Task.Delay(25, cancellationToken);
            else buffer = buffer[..Math.Min(buffer.Length, prefix.Length)];
            return await base.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            CopyDelayedAsync(destination, cancellationToken);
        private async Task CopyDelayedAsync(Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            int count;
            while ((count = await ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
