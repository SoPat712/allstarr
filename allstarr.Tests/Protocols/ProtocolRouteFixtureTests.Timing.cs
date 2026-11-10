using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Playlists;
using allstarr.Models.Lyrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task TimingMixRoutesExposeWorkWithoutChangingCachedBodies()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track,
                "mix-song", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new Uri("https://images.example.test/mix.png"));
        using var factory = ClientMixFactory(gateway, configure: UseTimingImageCache, nativeResponse: request => request.RequestUri!.Host == "images.example.test"
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(MixPicture()) { Headers = { ContentType = new("image/png") } } }
            : null);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var playlist = PlaylistVirtualizationService.CreateProtocolId(mix.Id);
        foreach (var (path, stages) in new (string, string[])[]
        {
            ("/allstarr/v1/mixes", ["build", "merge", "match", "serialize"]),
            ($"/allstarr/v1/mixes/{mix.Id}/items", ["build", "match", "serialize"]),
            ("/allstarr/v1/items/native-mix-song/appearances", ["match", "serialize"]),
            ($"/Items/{playlist}", ["build", "serialize"]),
            ($"/Playlists/{playlist}/Items", ["build", "serialize"]),
            ($"/Items/{playlist}/Images/Primary", ["resolve", "fetch", "render"]),
            ($"/allstarr/v1/mixes/{mix.Id}/cover", ["build", "resolve", "fetch", "render"])
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertTimingStages(response, stages);
            var body = await response.Content.ReadAsByteArrayAsync();
            factory.Services.GetRequiredService<IConfiguration>()["Diagnostics:ServerTimingEnabled"] = "false";
            using var without = await client.GetAsync(path);
            Assert.Equal(response.StatusCode, without.StatusCode);
            Assert.Equal(body, await without.Content.ReadAsByteArrayAsync());
            Assert.False(without.Headers.Contains("Server-Timing"));
            factory.Services.GetRequiredService<IConfiguration>()["Diagnostics:ServerTimingEnabled"] = "true";
        }
    }

    [Fact]
    public async Task TimingDetailsAndHistoryExposeTheirWork()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""")
            : Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Track"}]}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        client.DefaultRequestHeaders.Add("X-Emby-Device-Id", "phone");
        using var details = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native" } });
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        AssertTimingStages(details, "build", "match", "serialize", "lock");
        using var history = await client.GetAsync("/allstarr/v1/me/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        AssertTimingStages(history, "merge", "serialize");
    }

    [Fact]
    public async Task TimingCurrentPlaybackExposesDeviceAndOwnershipMatching()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        await SeedClientPlaybackAsync(factory, "local");
        await SetClientPlaybackAsync(factory, "listener", "phone", "native-song");
        using var request = ClientPlaybackRequest("/allstarr/v1/playback/current");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTimingStages(response, "match");
    }

    [Fact]
    public async Task TimingLyricsSourcesExposeLookupAndProjection()
    {
        var recording = Guid.NewGuid();
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<allstarr.Models.Domain.Song>(),
                It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()))
            .ReturnsAsync(new[] { new ProtocolLyricsCandidate("lrclib", new LyricsInfo { Source = "lrclib", SyncedLyrics = "[00:01]Known words" }) });
        using var factory = ClientLyricsFactory(recording, new(), configure: services =>
        {
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(resolver.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-lyrics/lyrics/sources");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTimingStages(response, "sources", "match", "serialize");
        resolver.Verify(service => service.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<allstarr.Models.Domain.Song>(),
            It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()), Times.Once);
    }

    [Fact]
    public async Task TimingLyricsSearchExposesMatchingAndIndexWaits()
    {
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/search/lyrics?q=words");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTimingStages(response, "match", "lock");
    }

    [Fact]
    public async Task TimingSearchKeepsDelayedBackendAndProviderCallsAndResponseIdentical()
    {
        var backendCalls = 0;
        var providerCalls = 0;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .Returns(async () =>
            {
                Interlocked.Increment(ref providerCalls);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Delay(800);
                allstarr.Core.Operations.RequestTiming.Current?.Add(allstarr.Core.Operations.RequestTimingStage.Provider,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));
                return new allstarr.Models.Search.SearchResult
                {
                    Songs = [new() { Id = "ext-deezer-song-track", Title = "Track", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "track" }]
                };
            });
        HttpResponseMessage Backend(HttpRequestMessage request)
        {
            Interlocked.Increment(ref backendCalls);
            return request.RequestUri!.AbsolutePath == "/Users/Me" ? Json(200, """{"Id":"listener"}""") :
                Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Track","Artists":["Artist"]}],"TotalRecordCount":1,"StartIndex":0}""");
        }
        using var factory = new ProtocolFactory("Jellyfin", Backend, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new TimingBackend(Backend)));
        });
        using var client = factory.CreateClient();
        const string path = "/Items?searchTerm=Track&IncludeItemTypes=Audio&Limit=40&ApiKey=listener";
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["Diagnostics:ServerTimingEnabled"] = "false";
        using (var warm = await client.GetAsync(path)) Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        backendCalls = providerCalls = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var before = await client.GetAsync(path);
        var body = await before.Content.ReadAsByteArrayAsync();
        var beforeMs = timer.Elapsed.TotalMilliseconds;
        var beforeCalls = backendCalls;
        Assert.Equal(1, providerCalls);
        Assert.False(before.Headers.Contains("Server-Timing"));
        configuration["Diagnostics:ServerTimingEnabled"] = "true";
        backendCalls = providerCalls = 0;
        timer.Restart();
        using var after = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(body, await after.Content.ReadAsByteArrayAsync());
        var afterMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(beforeCalls, backendCalls);
        Assert.Equal(1, providerCalls);
        AssertTimingStages(after, "library", "sources", "saved", "owned", "merge", "queue");
        _lyricsSearchOutput.WriteLine($"Timing comparison: disabled={beforeMs:F1} ms; enabled={afterMs:F1} ms; backend calls={beforeCalls}/{backendCalls}; provider calls=1/{providerCalls}; response bytes={body.Length}, identical.");
    }

    private sealed class TimingBackend(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(150, token);
            allstarr.Core.Operations.RequestTiming.Current?.Add(allstarr.Core.Operations.RequestTimingStage.Jellyfin,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return respond(request);
        }
    }

    private void AssertTimingStages(HttpResponseMessage response, params string[] names)
    {
        var header = Assert.Single(response.Headers.GetValues("Server-Timing"));
        foreach (var name in names) Assert.Contains(", " + name + ";dur=", header);
        var durations = header.Split(", ").Where(entry => entry.Contains(";dur=", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Split(';')[0], entry => double.Parse(entry.Split(';')[1][4..], CultureInfo.InvariantCulture));
        _lyricsSearchOutput.WriteLine($"{response.RequestMessage!.RequestUri!.AbsolutePath}: {header}");
        Assert.All(durations.Values, value => Assert.True(value >= 0));
        Assert.DoesNotContain("listener", header);
        Assert.DoesNotContain("native", header);
        Assert.DoesNotContain("mix-song", header);
    }
}
