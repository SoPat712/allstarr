<<<<<<< HEAD
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.SquidWTF;
using allstarr.Services.Common;
using allstarr.Models.Settings;
using System.Collections.Generic;

namespace allstarr.Tests;

public class SquidWTFMetadataServiceTests
{
    private readonly Mock<ILogger<SquidWTFMetadataService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly IOptions<SquidWTFSettings> _squidwtfSettings;
    private readonly Mock<RedisCacheService> _mockCache;
    private readonly List<string> _apiUrls;

    public SquidWTFMetadataServiceTests()
    {
        _mockLogger = new Mock<ILogger<SquidWTFMetadataService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        
        _subsonicSettings = Options.Create(new SubsonicSettings
        {
            ExplicitFilter = ExplicitFilter.All
        });
        
        _squidwtfSettings = Options.Create(new SquidWTFSettings
        {
            Quality = "FLAC"
        });
        
        // Create mock Redis cache
        var mockRedisLogger = new Mock<ILogger<RedisCacheService>>();
        var mockRedisSettings = Options.Create(new RedisSettings { Enabled = false });
        _mockCache = new Mock<RedisCacheService>(mockRedisSettings, mockRedisLogger.Object);
        
        _apiUrls = new List<string> 
        { 
            "https://squid.wtf",
            "https://mirror1.squid.wtf",
            "https://mirror2.squid.wtf"
        };
        
        var httpClient = new System.Net.Http.HttpClient();
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
    }

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_AcceptsOptionalGenreEnrichment()
    {
        // Arrange - GenreEnrichmentService is optional, just pass null

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls,
            null); // GenreEnrichmentService is optional

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void SearchSongsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchSongsAsync("Mr. Brightside", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchAlbumsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchAlbumsAsync("Hot Fuss", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchArtistsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchArtistsAsync("The Killers", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchPlaylistsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchPlaylistsAsync("Rock Classics", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetSongAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetSongAsync("squidwtf", "123456");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetAlbumAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetAlbumAsync("squidwtf", "789012");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetArtistAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetArtistAsync("squidwtf", "345678");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetArtistAlbumsAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetArtistAlbumsAsync("squidwtf", "345678");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPlaylistAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetPlaylistAsync("squidwtf", "playlist123");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPlaylistTracksAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetPlaylistTracksAsync("squidwtf", "playlist123");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchAllAsync_CombinesAllSearchTypes()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchAllAsync("The Killers", 20, 20, 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void ExplicitFilter_RespectsSettings()
    {
        // Arrange - Test with CleanOnly filter
        var cleanOnlySettings = Options.Create(new SubsonicSettings
        {
            ExplicitFilter = ExplicitFilter.CleanOnly
        });

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            cleanOnlySettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void MultipleApiUrls_EnablesRoundRobinFallback()
    {
        // Arrange
        var multipleUrls = new List<string>
        {
            "https://primary.squid.wtf",
            "https://backup1.squid.wtf",
            "https://backup2.squid.wtf",
            "https://backup3.squid.wtf"
        };

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            multipleUrls);

        // Assert
        Assert.NotNull(service);
    }
}
||||||| f68706f
=======
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.SquidWTF;
using allstarr.Services.Common;
using allstarr.Models.Settings;
using System.Collections.Generic;

namespace allstarr.Tests;

public class SquidWTFMetadataServiceTests
{
    private readonly Mock<ILogger<SquidWTFMetadataService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly IOptions<SquidWTFSettings> _squidwtfSettings;
    private readonly Mock<RedisCacheService> _mockCache;
    private readonly List<string> _apiUrls;

    public SquidWTFMetadataServiceTests()
    {
        _mockLogger = new Mock<ILogger<SquidWTFMetadataService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        
        _subsonicSettings = Options.Create(new SubsonicSettings
        {
            ExplicitFilter = ExplicitFilter.All
        });
        
        _squidwtfSettings = Options.Create(new SquidWTFSettings
        {
            Quality = "FLAC"
        });
        
        // Create mock Redis cache
        var mockRedisLogger = new Mock<ILogger<RedisCacheService>>();
        var mockRedisSettings = Options.Create(new RedisSettings { Enabled = false });
        _mockCache = new Mock<RedisCacheService>(mockRedisSettings, mockRedisLogger.Object);
        
        _apiUrls = new List<string> 
        { 
            "https://test1.example.com",
            "https://test2.example.com",
            "https://test3.example.com"
        };
        
        var httpClient = new System.Net.Http.HttpClient();
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
    }

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_AcceptsOptionalGenreEnrichment()
    {
        // Arrange - GenreEnrichmentService is optional, just pass null

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls,
            null); // GenreEnrichmentService is optional

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void SearchSongsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchSongsAsync("Mr. Brightside", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchAlbumsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchAlbumsAsync("Hot Fuss", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchArtistsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchArtistsAsync("The Killers", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchPlaylistsAsync_AcceptsQueryAndLimit()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchPlaylistsAsync("Rock Classics", 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetSongAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetSongAsync("squidwtf", "123456");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetAlbumAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetAlbumAsync("squidwtf", "789012");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetArtistAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetArtistAsync("squidwtf", "345678");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetArtistAlbumsAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetArtistAlbumsAsync("squidwtf", "345678");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPlaylistAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetPlaylistAsync("squidwtf", "playlist123");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPlaylistTracksAsync_RequiresProviderAndId()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.GetPlaylistTracksAsync("squidwtf", "playlist123");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void SearchAllAsync_CombinesAllSearchTypes()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Act
        var result = service.SearchAllAsync("The Killers", 20, 20, 20);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void ExplicitFilter_RespectsSettings()
    {
        // Arrange - Test with CleanOnly filter
        var cleanOnlySettings = Options.Create(new SubsonicSettings
        {
            ExplicitFilter = ExplicitFilter.CleanOnly
        });

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            cleanOnlySettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void MultipleApiUrls_EnablesRoundRobinFallback()
    {
        // Arrange
        var multipleUrls = new List<string>
        {
            "https://test-primary.example.com",
            "https://test-backup1.example.com",
            "https://test-backup2.example.com",
            "https://test-backup3.example.com"
        };

        // Act
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            multipleUrls);

        // Assert
        Assert.NotNull(service);
    }
}
>>>>>>> beta
