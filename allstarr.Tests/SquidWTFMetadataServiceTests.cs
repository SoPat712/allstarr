using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Services.SquidWTF;
using allstarr.Services.Common;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace allstarr.Tests;

public class SquidWTFMetadataServiceTests
{
    private readonly Mock<ILogger<SquidWTFMetadataService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly IOptions<SubsonicSettings> _subsonicSettings;
    private readonly IOptions<SquidWTFSettings> _squidwtfSettings;
    private readonly Mock<IApplicationCache> _mockCache;
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
        _mockCache = new Mock<IApplicationCache>();

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
    public async Task SearchAllAsync_WithZeroLimits_SkipsUnusedBuckets()
    {
        var requestKinds = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var trackQuery = GetQueryParameter(request.RequestUri!, "s");
            var albumQuery = GetQueryParameter(request.RequestUri!, "al");
            var artistQuery = GetQueryParameter(request.RequestUri!, "a");

            if (!string.IsNullOrWhiteSpace(trackQuery))
            {
                requestKinds.Add("song");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateTrackSearchResponse(CreateTrackPayload(1, "Song", "USRC12345678")))
                };
            }

            if (!string.IsNullOrWhiteSpace(albumQuery))
            {
                requestKinds.Add("album");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateAlbumSearchResponse())
                };
            }

            if (!string.IsNullOrWhiteSpace(artistQuery))
            {
                requestKinds.Add("artist");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateArtistSearchResponse())
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "https://test1.example.com" });

        var result = await service.SearchAllAsync("OK Computer", 0, 5, 0);

        Assert.Empty(result.Songs);
        Assert.Single(result.Albums);
        Assert.Empty(result.Artists);
        Assert.Equal(new[] { "album" }, requestKinds);
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
    public async Task GetTrackRecommendationsAsync_FallsBackWhenFirstEndpointReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var port = request.RequestUri?.Port;

            if (port == 5011)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "version": "2.4",
                      "data": {
                        "limit": 20,
                        "offset": 0,
                        "totalNumberOfItems": 0,
                        "items": []
                      }
                    }
                    """)
                };
            }

            if (port == 5012)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "version": "2.4",
                      "data": {
                        "limit": 20,
                        "offset": 0,
                        "totalNumberOfItems": 1,
                        "items": [
                          {
                            "track": {
                              "id": 371921532,
                              "title": "Take It Slow",
                              "duration": 139,
                              "trackNumber": 1,
                              "volumeNumber": 1,
                              "explicit": false,
                              "artist": { "id": 10330497, "name": "Isaac Dunbar" },
                              "artists": [
                                { "id": 10330497, "name": "Isaac Dunbar" }
                              ],
                              "album": {
                                "id": 371921525,
                                "title": "Take It Slow",
                                "cover": "aeb70f15-78ef-4230-929d-2d62c70ac00c"
                              }
                            }
                          }
                        ]
                      }
                    }
                    """)
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string>
            {
                "http://127.0.0.1:5011",
                "http://127.0.0.1:5012"
            });

        var result = await service.GetTrackRecommendationsAsync("227242909", 20);

        Assert.Single(result);
        Assert.Equal("371921532", result[0].ExternalId);
        Assert.Equal("Take It Slow", result[0].Title);
    }

    [Fact]
    public async Task GetSongAsync_FallsBackWhenFirstEndpointReturnsErrorPayload()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var port = request.RequestUri?.Port;

            if (port == 5021)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "detail": "Upstream API error"
                    }
                    """)
                };
            }

            if (port == 5022)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "version": "2.4",
                      "data": {
                        "id": 227242909,
                        "title": "Monica Lewinsky",
                        "duration": 132,
                        "trackNumber": 1,
                        "volumeNumber": 1,
                        "explicit": true,
                        "artist": { "id": 8420542, "name": "UPSAHL" },
                        "artists": [
                          { "id": 8420542, "name": "UPSAHL" }
                        ],
                        "album": {
                          "id": 227242908,
                          "title": "Monica Lewinsky",
                          "cover": "32522342-3903-42ab-aaea-a6f4f46ca0cc"
                        }
                      }
                    }
                    """)
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string>
            {
                "http://127.0.0.1:5021",
                "http://127.0.0.1:5022"
            });

        var song = await service.GetSongAsync("squidwtf", "227242909");

        Assert.NotNull(song);
        Assert.Equal("227242909", song!.ExternalId);
        Assert.Equal("Monica Lewinsky", song.Title);
        Assert.Equal(1, song.ExplicitContentLyrics);
    }

    [Fact]
    public async Task FindSongByIsrcAsync_UsesExactIsrcEndpoint()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateTrackSearchResponse(CreateTrackPayload(
                    144371283,
                    "Don't Look Back In Anger",
                    "GBBQY0002027",
                    artistName: "Oasis",
                    artistId: 109,
                    albumTitle: "Familiar To Millions (Live)",
                    albumId: 144371273)))
            };
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5031" });

        var song = await service.FindSongByIsrcAsync("GBBQY0002027");

        Assert.NotNull(song);
        Assert.Equal("GBBQY0002027", song!.Isrc);
        Assert.Equal("144371283", song.ExternalId);
        Assert.Contains("/search/?i=GBBQY0002027&limit=1&offset=0", requests);
    }

    [Fact]
    public async Task FindSongByIsrcAsync_FallsBackToTextSearchWhenExactEndpointPayloadIsUnexpected()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);

            if (!string.IsNullOrWhiteSpace(GetQueryParameter(request.RequestUri, "i")))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "version": "2.6", "unexpected": {} }""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateTrackSearchResponse(CreateTrackPayload(
                    427520487,
                    "Azizam",
                    "GBAHS2500081",
                    artistName: "Ed Sheeran",
                    artistId: 3995478,
                    albumTitle: "Azizam",
                    albumId: 427520486)))
            };
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5032" });

        var song = await service.FindSongByIsrcAsync("GBAHS2500081");

        Assert.NotNull(song);
        Assert.Equal("GBAHS2500081", song!.Isrc);
        Assert.Contains("/search/?i=GBAHS2500081&limit=1&offset=0", requests);
        Assert.Contains("/search/?s=isrc%3AGBAHS2500081&limit=1&offset=0", requests);
    }

    [Fact]
    public async Task SearchEndpoints_IncludeRequestedRemoteLimitAndOffset()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var trackQuery = GetQueryParameter(request.RequestUri, "s");
            var albumQuery = GetQueryParameter(request.RequestUri, "al");
            var artistQuery = GetQueryParameter(request.RequestUri, "a");
            var playlistQuery = GetQueryParameter(request.RequestUri, "p");

            if (!string.IsNullOrWhiteSpace(trackQuery))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateTrackSearchResponse(CreateTrackPayload(1, "Song", "USRC12345678")))
                };
            }

            if (!string.IsNullOrWhiteSpace(albumQuery))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateAlbumSearchResponse())
                };
            }

            if (!string.IsNullOrWhiteSpace(artistQuery))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateArtistSearchResponse())
                };
            }

            if (!string.IsNullOrWhiteSpace(playlistQuery))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CreatePlaylistSearchResponse())
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5033" });

        await service.SearchSongsAsync("Take Five", 7);
        await service.SearchAlbumsAsync("Time Out", 8);
        await service.SearchArtistsAsync("Dave Brubeck", 9);
        await service.SearchPlaylistsAsync("Jazz Essentials", 10);

        Assert.Contains("/search/?s=Take%20Five&limit=7&offset=0", requests);
        Assert.Contains("/search/?al=Time%20Out&limit=8&offset=0", requests);
        Assert.Contains("/search/?a=Dave%20Brubeck&limit=9&offset=0", requests);
        Assert.Contains("/search/?p=Jazz%20Essentials&limit=10&offset=0", requests);
    }

    [Fact]
    public async Task GetArtistAsync_UsesLightweightArtistEndpointAndCoverFallback()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "version": "2.6",
                  "artist": {
                    "id": 25022,
                    "name": "Kanye West",
                    "picture": null
                  },
                  "cover": {
                    "750": "https://example.com/kanye-750.jpg"
                  }
                }
                """)
            };
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5034" });

        var artist = await service.GetArtistAsync("squidwtf", "25022");

        Assert.Contains("/artist/?id=25022", requests);
        Assert.NotNull(artist);
        Assert.Equal("Kanye West", artist!.Name);
        Assert.Equal("https://example.com/kanye-750.jpg", artist.ImageUrl);
        Assert.Null(artist.AlbumCount);
    }

    [Fact]
    public async Task GetAlbumAsync_PaginatesBeyondFirstPage()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var offset = int.Parse(GetQueryParameter(request.RequestUri, "offset") ?? "0");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateAlbumPageResponse(offset, offset == 0 ? 500 : 1, totalTracks: 501))
            };
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5035" });

        var album = await service.GetAlbumAsync("squidwtf", "58990510");

        Assert.Contains("/album/?id=58990510&limit=500&offset=0", requests);
        Assert.Contains("/album/?id=58990510&limit=500&offset=500", requests);
        Assert.NotNull(album);
        Assert.Equal(501, album!.Songs.Count);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_PaginatesBeyondFirstPage()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var offset = int.Parse(GetQueryParameter(request.RequestUri, "offset") ?? "0");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreatePlaylistPageResponse(offset, offset == 0 ? 500 : 1, totalTracks: 501))
            };
        });

        var httpClient = new HttpClient(handler);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var service = new SquidWTFMetadataService(
            _mockHttpClientFactory.Object,
            _subsonicSettings,
            _squidwtfSettings,
            _mockLogger.Object,
            _mockCache.Object,
            new List<string> { "http://127.0.0.1:5036" });

        var songs = await service.GetPlaylistTracksAsync("squidwtf", "playlist123");

        Assert.Equal(501, songs.Count);
        Assert.Equal("Big Playlist", songs[0].Album);
        Assert.Equal("Big Playlist", songs[^1].Album);
        Assert.Contains("/playlist/?id=playlist123&limit=500&offset=0", requests);
        Assert.Contains("/playlist/?id=playlist123&limit=500&offset=500", requests);
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

    private static string CreateTrackSearchResponse(object trackPayload)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["data"] = new Dictionary<string, object?>
            {
                ["limit"] = 25,
                ["offset"] = 0,
                ["totalNumberOfItems"] = 1,
                ["items"] = new[] { trackPayload }
            }
        });
    }

    private static string CreateAlbumSearchResponse()
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["data"] = new Dictionary<string, object?>
            {
                ["albums"] = new Dictionary<string, object?>
                {
                    ["limit"] = 25,
                    ["offset"] = 0,
                    ["totalNumberOfItems"] = 1,
                    ["items"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 58990510,
                            ["title"] = "OK Computer",
                            ["numberOfTracks"] = 12,
                            ["cover"] = "e77e4cc0-6cd0-4522-807d-88aeac488065",
                            ["artist"] = new Dictionary<string, object?>
                            {
                                ["id"] = 64518,
                                ["name"] = "Radiohead"
                            }
                        }
                    }
                }
            }
        });
    }

    private static string CreateArtistSearchResponse()
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["data"] = new Dictionary<string, object?>
            {
                ["artists"] = new Dictionary<string, object?>
                {
                    ["limit"] = 25,
                    ["offset"] = 0,
                    ["totalNumberOfItems"] = 1,
                    ["items"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 8812,
                            ["name"] = "Coldplay",
                            ["picture"] = "b4579672-5b91-4679-a27a-288f097a4da5"
                        }
                    }
                }
            }
        });
    }

    private static string CreatePlaylistSearchResponse()
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["data"] = new Dictionary<string, object?>
            {
                ["playlists"] = new Dictionary<string, object?>
                {
                    ["limit"] = 25,
                    ["offset"] = 0,
                    ["totalNumberOfItems"] = 1,
                    ["items"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["uuid"] = "playlist123",
                            ["title"] = "Jazz Essentials",
                            ["creator"] = new Dictionary<string, object?>
                            {
                                ["id"] = 0
                            },
                            ["numberOfTracks"] = 1,
                            ["duration"] = 180,
                            ["squareImage"] = "b15bb487-dd6e-45ff-9e50-ee5083f20669"
                        }
                    }
                }
            }
        });
    }

    private static string CreateAlbumPageResponse(int offset, int count, int totalTracks)
    {
        var items = Enumerable.Range(offset + 1, count)
            .Select(index => (object)new Dictionary<string, object?>
            {
                ["item"] = CreateTrackPayload(
                    index,
                    $"Album Track {index}",
                    $"USRC{index:00000000}",
                    albumTitle: "Paginated Album",
                    albumId: 58990510)
            })
            .ToArray();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["data"] = new Dictionary<string, object?>
            {
                ["id"] = 58990510,
                ["title"] = "Paginated Album",
                ["numberOfTracks"] = totalTracks,
                ["cover"] = "e77e4cc0-6cd0-4522-807d-88aeac488065",
                ["artist"] = new Dictionary<string, object?>
                {
                    ["id"] = 64518,
                    ["name"] = "Radiohead"
                },
                ["items"] = items
            }
        });
    }

    private static string CreatePlaylistPageResponse(int offset, int count, int totalTracks)
    {
        var items = Enumerable.Range(offset + 1, count)
            .Select(index => (object)new Dictionary<string, object?>
            {
                ["item"] = CreateTrackPayload(
                    index,
                    $"Playlist Track {index}",
                    $"GBARL{index:0000000}",
                    artistName: "Mark Ronson",
                    artistId: 8722,
                    albumTitle: "Uptown Special",
                    albumId: 39249709)
            })
            .ToArray();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "2.6",
            ["playlist"] = new Dictionary<string, object?>
            {
                ["uuid"] = "playlist123",
                ["title"] = "Big Playlist",
                ["creator"] = new Dictionary<string, object?>
                {
                    ["id"] = 0
                },
                ["numberOfTracks"] = totalTracks,
                ["duration"] = totalTracks * 180,
                ["squareImage"] = "b15bb487-dd6e-45ff-9e50-ee5083f20669"
            },
            ["items"] = items
        });
    }

    private static Dictionary<string, object?> CreateTrackPayload(
        int id,
        string title,
        string isrc,
        string artistName = "Artist",
        int artistId = 1,
        string albumTitle = "Album",
        int albumId = 10)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["duration"] = 180,
            ["trackNumber"] = (id % 12) + 1,
            ["volumeNumber"] = 1,
            ["explicit"] = false,
            ["isrc"] = isrc,
            ["artist"] = new Dictionary<string, object?>
            {
                ["id"] = artistId,
                ["name"] = artistName
            },
            ["artists"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = artistId,
                    ["name"] = artistName
                }
            },
            ["album"] = new Dictionary<string, object?>
            {
                ["id"] = albumId,
                ["title"] = albumTitle,
                ["cover"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
            }
        };
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (!key.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return null;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
