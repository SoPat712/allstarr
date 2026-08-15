using System.Net;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Logging;
using allstarr.Services.Lyrics;
using allstarr.Services.Common;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public class LrclibServiceTests
{
    private readonly Mock<ILogger<LrclibService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IApplicationCache> _mockCache;
    private readonly Mock<IManualLyricsMappingStore> _mockMappingStore;
    private readonly Mock<HttpMessageHandler> _handler;

    public LrclibServiceTests()
    {
        _mockLogger = new Mock<ILogger<LrclibService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        // Create mock shared cache and durable manual-decision store.
        _mockCache = new Mock<IApplicationCache>();
        _mockMappingStore = new Mock<IManualLyricsMappingStore>();

        _handler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri("https://lrclib.net")
        };

        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
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
            """{"id":7,"trackName":"Cached","artistName":"Artist","plainLyrics":"cached lyrics"}""");
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

    private LrclibService CreateService() =>
        new(
            _mockHttpClientFactory.Object,
            _mockCache.Object,
            _mockMappingStore.Object,
            _mockLogger.Object);
}
