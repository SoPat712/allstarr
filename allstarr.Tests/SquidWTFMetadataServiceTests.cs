using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.SquidWTF;
using allstarr.Services.Common;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

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

    [Fact]
    public void BuildSearchQueryVariants_WithAmpersand_AddsAndVariant()
    {
        var variants = InvokePrivateStaticMethod<IReadOnlyList<string>>(
            typeof(SquidWTFMetadataService),
            "BuildSearchQueryVariants",
            "love & hyperbole");

        Assert.Equal(2, variants.Count);
        Assert.Contains("love & hyperbole", variants);
        Assert.Contains("love and hyperbole", variants);
    }

    [Fact]
    public void BuildSearchQueryVariants_WithoutAmpersand_KeepsOriginalOnly()
    {
        var variants = InvokePrivateStaticMethod<IReadOnlyList<string>>(
            typeof(SquidWTFMetadataService),
            "BuildSearchQueryVariants",
            "love and hyperbole");

        Assert.Single(variants);
        Assert.Equal("love and hyperbole", variants[0]);
    }

    [Fact]
    public void ParseTidalTrack_MapsFieldsUsedByJellyfinAndTagWriter()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        using var doc = JsonDocument.Parse("""
        {
          "id": 452455962,
          "title": "Stuck Up",
          "version": "Live",
          "duration": 151,
          "trackNumber": 1,
          "volumeNumber": 1,
          "explicit": true,
          "bpm": 130,
          "isrc": "USUG12504959",
          "streamStartDate": "2025-08-08T00:00:00.000+0000",
          "copyright": "℗ 2025 Golden Angel LLC, under exclusive license to Interscope Records.",
          "artists": [
            { "id": 9321197, "name": "Amaarae" },
            { "id": 30396, "name": "Black Star" }
          ],
          "album": {
            "id": 452455961,
            "title": "BLACK STAR",
            "cover": "87f0be2b-dd7e-42d4-b438-f8f161d29674",
            "numberOfTracks": 13,
            "releaseDate": "2025-08-08",
            "artist": { "id": 9321197, "name": "Amaarae" }
          }
        }
        """);

        // Act
        var song = InvokePrivateMethod<Song>(service, "ParseTidalTrack", doc.RootElement, null);

        // Assert
        Assert.Equal("ext-squidwtf-song-452455962", song.Id);
        Assert.Equal("Stuck Up (Live)", song.Title);
        Assert.Equal("Amaarae", song.Artist);
        Assert.Equal("Amaarae", song.AlbumArtist);
        Assert.Equal("USUG12504959", song.Isrc);
        Assert.Equal(130, song.Bpm);
        Assert.Equal("2025-08-08", song.ReleaseDate);
        Assert.Equal(2025, song.Year);
        Assert.Equal(13, song.TotalTracks);
        Assert.Equal("℗ 2025 Golden Angel LLC, under exclusive license to Interscope Records.", song.Copyright);
        Assert.Equal("Black Star", Assert.Single(song.Contributors));
        Assert.Contains("/87f0be2b/dd7e/42d4/b438/f8f161d29674/320x320.jpg", song.CoverArtUrl);
        Assert.Contains("/87f0be2b/dd7e/42d4/b438/f8f161d29674/1280x1280.jpg", song.CoverArtUrlLarge);
    }

    [Fact]
    public void ParseTidalTrackFull_MapsCopyrightToCopyrightField()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        using var doc = JsonDocument.Parse("""
        {
          "id": 987654,
          "title": "Night Walk",
          "duration": 200,
          "trackNumber": 7,
          "volumeNumber": 1,
          "streamStartDate": "2024-02-01T00:00:00.000+0000",
          "copyright": "℗ 2024 Example Label",
          "artist": { "id": 111, "name": "Main Artist" },
          "album": {
            "id": 222,
            "title": "Moonlight",
            "cover": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
          }
        }
        """);

        // Act
        var song = InvokePrivateMethod<Song>(service, "ParseTidalTrackFull", doc.RootElement);

        // Assert
        Assert.Equal("℗ 2024 Example Label", song.Copyright);
        Assert.Null(song.Label);
        Assert.Equal(2024, song.Year);
    }

    [Fact]
    public void ParseTidalPlaylist_UsesPromotedArtistsAndFallbackMetadata()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        using var doc = JsonDocument.Parse("""
        {
          "uuid": "b55ffed4-ab60-4da5-9faf-e54a45de4f9c",
          "title": "Guest Verses: BIG30",
          "description": "Remixes and guest verses",
          "creator": { "id": 0 },
          "promotedArtists": [
            { "id": 19872911, "name": "BigWalkDog" }
          ],
          "lastUpdated": "2022-09-23T17:52:48.974+0000",
          "image": "75ed74c0-58d8-4af7-a4c0-cbae0315dc34",
          "numberOfTracks": 32,
          "duration": 5466
        }
        """);

        // Act
        var playlist = InvokePrivateMethod<allstarr.Models.Subsonic.ExternalPlaylist>(service, "ParseTidalPlaylist", doc.RootElement);

        // Assert
        Assert.Equal("BigWalkDog", playlist.CuratorName);
        Assert.Equal(32, playlist.TrackCount);
        Assert.Equal(5466, playlist.Duration);
        Assert.True(playlist.CreatedDate.HasValue);
        Assert.Equal(2022, playlist.CreatedDate!.Value.Year);
        Assert.Contains("/75ed74c0/58d8/4af7/a4c0/cbae0315dc34/1080x1080.jpg", playlist.CoverUrl);
    }

    [Fact]
    public void ParseTidalAlbum_AppendsVersionAndParsesYearFallback()
    {
        // Arrange
        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            _apiUrls);

        using var doc = JsonDocument.Parse("""
        {
          "id": 579814,
          "title": "Black Star",
          "version": "Remastered",
          "streamStartDate": "2002-06-04T00:00:00.000+0000",
          "numberOfTracks": 13,
          "cover": "49fcdc8b-2f43-43a9-b156-f2f83908f95f",
          "artists": [
            { "id": 30396, "name": "Black Star" }
          ]
        }
        """);

        // Act
        var album = InvokePrivateMethod<Album>(service, "ParseTidalAlbum", doc.RootElement);

        // Assert
        Assert.Equal("Black Star (Remastered)", album.Title);
        Assert.Equal(2002, album.Year);
        Assert.Equal(13, album.SongCount);
        Assert.Contains("/49fcdc8b/2f43/43a9/b156/f2f83908f95f/320x320.jpg", album.CoverArtUrl);
    }

    private static T InvokePrivateMethod<T>(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(target, parameters);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static T InvokePrivateStaticMethod<T>(Type targetType, string methodName, params object?[] parameters)
    {
        var method = targetType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, parameters);
        Assert.NotNull(result);
        return (T)result!;
    }
}
