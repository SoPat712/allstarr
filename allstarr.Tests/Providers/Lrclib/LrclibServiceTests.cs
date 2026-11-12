using allstarr.Core.Caching;
using System.Net;
using System.Text.Json;
using allstarr.Core.Providers.Lyrics;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Logging;
using allstarr.Services.Lyrics;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public class LrclibServiceTests
{
    private readonly Mock<ILogger<LrclibService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IApplicationCache> _mockCache;
    private readonly Mock<HttpMessageHandler> _handler;

    public LrclibServiceTests()
    {
        _mockLogger = new Mock<ILogger<LrclibService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        _mockCache = new Mock<IApplicationCache>();

        _handler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri("https://lrclib.net")
        };

        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
    }

    [Fact]
    public async Task SearchTriesArtistTitleReadingAndRejectsInstrumentalAndWrongNumbers()
    {
        _handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()).Returns((HttpRequestMessage request, CancellationToken _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.Query.Contains("track_name=Part%202&", StringComparison.Ordinal)
                        ? """
                            [{"id":1,"trackName":"Part 3","artistName":"Artist","duration":180,"plainLyrics":"wrong"},
                            {"id":2,"trackName":"Part 2","artistName":"Artist","albumName":"Karaoke","duration":180,"plainLyrics":"wrong"},
                            {"id":3,"trackName":"Part 2","artistName":"Artist","duration":180,"plainLyrics":"selected"}]
                            """
                        : "[]")
                }));
        var result = await CreateService().GetLyricsAsync("Artist - Part 2", "Unknown", "Album", 180);
        Assert.Equal("selected", result?.PlainLyrics);
        Assert.Single(result!.Versions!);
    }

    [Fact]
    public async Task GetLyricsAsync_WithMissingTrack_DoesNotCallProvider()
    {
        var service = CreateService();

        var result = await service.GetLyricsAsync("", "Artist", "Album", 180);

        Assert.Null(result);
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsCachedAsync_ReturnsProviderResult()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/get-cached"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"trackName":"Rocket","artistName":"Beyoncé","albumName":"4","duration":244,"plainLyrics":"plain","syncedLyrics":"[00:01]line"}""")
            });
        var service = CreateService();

        var result = await service.GetLyricsCachedAsync("Rocket", "Beyoncé", "4", 244);

        Assert.NotNull(result);
        Assert.Equal(42, result.Id);
        Assert.Equal("plain", result.PlainLyrics);
        Assert.Equal("[00:01]line", result.SyncedLyrics);
        _handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.Query.Contains("track_name=Rocket", StringComparison.Ordinal)),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_ReturnsApplicationCacheWithoutProviderCall()
    {
        _mockCache.Setup(cache => cache.GetStringAsync(It.IsAny<string>())).ReturnsAsync(
            """{"id":7,"trackName":"Cached","artistName":"Artist","plainLyrics":"cached lyrics","versions":[{"id":7,"plainLyrics":"cached lyrics"}]}""");
        var service = CreateService();

        var result = await service.GetLyricsAsync("Cached", "Artist", "Album", 180);

        Assert.NotNull(result);
        Assert.Equal("cached lyrics", result.PlainLyrics);
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_CacheOnlyMissDoesNotCallProvider()
    {
        _mockCache.Setup(cache => cache.GetStringAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        var service = CreateService();

        var result = await service.GetLyricsAsync("Missing", ["Artist"], "Album", 180,
            LyricsCompletionMode.CacheOnly, CancellationToken.None);

        Assert.Null(result);
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_CacheOnlyHitReturnsWithoutProviderCall()
    {
        _mockCache.Setup(cache => cache.GetStringAsync(It.IsAny<string>())).ReturnsAsync(
            """{"id":7,"trackName":"Cached","artistName":"Artist","plainLyrics":"cached lyrics","versions":[{"id":7,"plainLyrics":"cached lyrics"}]}""");
        var service = CreateService();

        var result = await service.GetLyricsAsync("Cached", ["Artist"], "Album", 180,
            LyricsCompletionMode.CacheOnly, CancellationToken.None);

        Assert.Equal("cached lyrics", result!.PlainLyrics);
        _handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_MissingRecordIdsRetainDistinctMetadataVersions()
    {
        SetupSearch("""[{"trackName":"Track","artistName":"Artist","duration":180,"plainLyrics":"Studio"},{"trackName":"Track (Live)","artistName":"Artist","duration":271,"plainLyrics":"Concert"}]""");
        var result = await CreateService().GetLyricsAsync("Track", "Artist", "Album", 180);
        Assert.Equal(2, result!.Versions!.Count);
        Assert.All(result.Versions!, version => Assert.Null(version.Metadata!.VersionKey));
    }

    [Fact]
    public async Task GetLyricsAsync_OrdersAndBoundsVersionsWithoutChangingBestMatch()
    {
        var records = Enumerable.Range(1, 7).Select(id => new
        {
            id,
            trackName = "Track",
            artistName = "Artist",
            albumName = (string?)null,
            duration = 180.125,
            plainLyrics = "plain " + id,
            syncedLyrics = id == 7 ? "[00:01]best" : null
        }).ToArray();
        SetupSearch(JsonSerializer.Serialize(records.Concat([records[6]])));

        var result = await CreateService().GetLyricsAsync("Track", "Artist", "Requested album", 180);

        Assert.NotNull(result);
        Assert.Equal(7, result.Id);
        Assert.Equal("[00:01]best", result.SyncedLyrics);
        Assert.Equal(new[] { 7, 1, 2, 3, 4 }, result.Versions!.Select(version => version.Id));
        Assert.All(result.Versions!, version => Assert.Null(version.Versions));
        Assert.Equal("7", result.Metadata!.VersionKey);
        Assert.Equal("Track", result.Metadata.Title);
        Assert.Equal("Artist", result.Metadata.Artist);
        Assert.Null(result.Metadata.Album);
        Assert.Equal(180125L, result.Metadata.DurationMs);
        Assert.Equal("Requested album", result.AlbumName);
        _handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_CachesAllVersionsWithTheSharedLyricsTtl()
    {
        var values = new Dictionary<string, string>();
        _mockCache.Setup(cache => cache.GetStringAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => values.GetValueOrDefault(key));
        _mockCache.Setup(cache => cache.SetStringAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Callback<string, string, TimeSpan?>((key, value, _) => values[key] = value)
            .ReturnsAsync(true);
        SetupSearch("""[{"id":1,"trackName":"Track","artistName":"Artist","duration":180,"plainLyrics":"first"},{"id":2,"trackName":"Track","artistName":"Artist","duration":180,"plainLyrics":"second"}]""");
        var service = CreateService();

        var first = await service.GetLyricsAsync("Track", "Artist", "Album", 180);
        var second = await service.GetLyricsAsync("Track", "Artist", "Album", 180);

        Assert.Equal(new[] { 1, 2 }, first!.Versions!.Select(version => version.Id));
        Assert.Equal(new[] { 1, 2 }, second!.Versions!.Select(version => version.Id));
        _handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        _mockCache.Verify(cache => cache.SetStringAsync(
            "lrclib:automatic:v2:" + CacheKeyBuilder.BuildLyricsKey("Artist", "Track", "Album", 180),
            It.IsAny<string>(), new CacheSettings().LyricsTTL), Times.Once());
    }

    [Fact]
    public async Task GetLyricsAsync_RefreshesLegacyBestOnlyCacheToDiscoverVersions()
    {
        _mockCache.Setup(cache => cache.GetStringAsync(It.IsAny<string>())).ReturnsAsync(
            """{"id":1,"plainLyrics":"old best"}""");
        SetupSearch("""[{"id":1,"trackName":"Track","artistName":"Artist","duration":180,"plainLyrics":"first"},{"id":2,"trackName":"Track","artistName":"Artist","duration":180,"plainLyrics":"second"}]""");

        var result = await CreateService().GetLyricsAsync("Track", "Artist", "Album", 180);

        Assert.Equal(new[] { 1, 2 }, result!.Versions!.Select(version => version.Id));
        _handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetLyricsAsync_ExactFallbackPreservesNullableProviderMetadata()
    {
        SetupSearch("""[{"id":3,"trackName":"ZZZZZZZZZZZZZZZZZZZZZZZZZZ","artistName":"YYYYYYY","duration":800,"plainLyrics":"wrong"}]""");
        _handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/get"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"plainLyrics":"exact"}""")
            });

        var result = await CreateService().GetLyricsAsync("Track", "Artist", "Album", 180);

        Assert.NotNull(result);
        Assert.Equal("exact", result.PlainLyrics);
        Assert.Equal("Track", result.TrackName);
        Assert.Equal("Artist", result.ArtistName);
        Assert.Equal("Album", result.AlbumName);
        Assert.Equal(180, result.Duration);
        Assert.Equal("42", result.Metadata!.VersionKey);
        Assert.Null(result.Metadata.Title);
        Assert.Null(result.Metadata.Artist);
        Assert.Null(result.Metadata.Album);
        Assert.Null(result.Metadata.DurationMs);
        Assert.Equal(42, Assert.Single(result.Versions!).Id);
    }

    [Fact]
    public async Task GetLyricsByIdAsync_ReturnsTheCompleteDocument()
    {
        _mockCache.Setup(cache => cache.SetStringAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>())).ReturnsAsync(true);
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/get/42"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":42,"trackName":"Mapped","artistName":"Artist","plainLyrics":"complete lyrics"}""")
            });
        var service = CreateService();

        var result = await service.GetLyricsByIdAsync(42);

        Assert.NotNull(result);
        Assert.Equal("complete lyrics", result.PlainLyrics);
    }

    [Fact]
    public async Task LegacyGlobalLyricsCacheCannotOverrideAutomaticLookup()
    {
        var requestedUris = new List<Uri>();
        _mockCache.Setup(cache => cache.SetStringAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>())).ReturnsAsync(true);
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => requestedUris.Add(request.RequestUri!))
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/search" ? "[]" :
                    """{"id":42,"trackName":"Mapped","artistName":"Artist","plainLyrics":"complete lyrics"}""")
            });
        var service = CreateService();
        var cacheKey = CacheKeyBuilder.BuildLyricsKey("Artist", "Mapped", "Album", 180);
        _mockCache.Setup(cache => cache.GetStringAsync(cacheKey))
            .ReturnsAsync("""{"id":999,"plainLyrics":"Legacy global words"}""");

        var result = await service.GetLyricsAsync("Mapped", "Artist", "Album", 180);

        Assert.Equal(new[] { "/api/search", "/api/get" }, requestedUris.Select(uri => uri.AbsolutePath));
        Assert.All(requestedUris, uri => Assert.Contains("track_name=Mapped", uri.Query, StringComparison.Ordinal));
        Assert.Equal("complete lyrics", result?.PlainLyrics);
        _mockCache.Verify(cache => cache.SetStringAsync(
            "lrclib:automatic:v2:" + cacheKey,
            It.Is<string>(value => IsCompleteLyricsJson(value)),
            new CacheSettings().LyricsTTL), Times.Once());
    }

    private static bool IsCompleteLyricsJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.GetProperty("id").GetInt32() == 42 &&
               document.RootElement.GetProperty("plainLyrics").GetString() == "complete lyrics";
    }

    private void SetupSearch(string json) =>
        _handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/search"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

    private LrclibService CreateService() =>
        new(
            _mockHttpClientFactory.Object,
            _mockCache.Object,
            Options.Create(new CacheSettings()),
            _mockLogger.Object);
}
