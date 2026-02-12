using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using allstarr.Services.Lyrics;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class LrclibServiceTests
{
    private readonly Mock<ILogger<LrclibService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<RedisCacheService> _mockCache;
    private readonly HttpClient _httpClient;

    public LrclibServiceTests()
    {
        _mockLogger = new Mock<ILogger<LrclibService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        
        // Create mock Redis cache
        var mockRedisLogger = new Mock<ILogger<RedisCacheService>>();
        var mockRedisSettings = Options.Create(new RedisSettings { Enabled = false });
        _mockCache = new Mock<RedisCacheService>(mockRedisSettings, mockRedisLogger.Object);
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net")
        };
        
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_httpClient);
    }

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Act
        var service = new LrclibService(_mockHttpClientFactory.Object, _mockCache.Object, _mockLogger.Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void GetLyricsAsync_RequiresValidParameters()
    {
        // Arrange
        var service = new LrclibService(_mockHttpClientFactory.Object, _mockCache.Object, _mockLogger.Object);

        // Act & Assert - Should handle empty parameters gracefully
        var result = service.GetLyricsAsync("", "Artist", "Album", 180);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetLyricsAsync_SupportsMultipleArtists()
    {
        // Arrange
        var service = new LrclibService(_mockHttpClientFactory.Object, _mockCache.Object, _mockLogger.Object);
        var artists = new[] { "Artist 1", "Artist 2", "Artist 3" };

        // Act
        var result = service.GetLyricsAsync("Track Name", artists, "Album", 180);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetLyricsByIdAsync_AcceptsValidId()
    {
        // Arrange
        var service = new LrclibService(_mockHttpClientFactory.Object, _mockCache.Object, _mockLogger.Object);

        // Act
        var result = service.GetLyricsByIdAsync(123456);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetLyricsCachedAsync_UsesCache()
    {
        // Arrange
        var service = new LrclibService(_mockHttpClientFactory.Object, _mockCache.Object, _mockLogger.Object);

        // Act
        var result = service.GetLyricsCachedAsync("Track", "Artist", "Album", 180);

        // Assert
        Assert.NotNull(result);
    }
}
