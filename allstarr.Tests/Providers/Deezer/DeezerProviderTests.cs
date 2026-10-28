using allstarr.Core.Providers.Deezer;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace allstarr.Tests;

public class DeezerProviderTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private DeezerProvider _service;

    public DeezerProviderTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _service = CreateService();
    }

    private DeezerProvider CreateService()
    {
        return new DeezerProvider(new DeezerHttpClient(_httpClientFactoryMock.Object.CreateClient(), 0, delay: (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task SearchSongsAsync_ReturnsListOfSongs()
    {
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 123456,
                    title = "Test Song",
                    isrc = "TESTISRC1234",
                    duration = 180,
                    track_position = 1,
                    artist = new { id = 789, name = "Test Artist" },
                    album = new { id = 456, title = "Test Album", cover_medium = "https://example.com/cover.jpg", cover_xl = "https://example.com/cover-xl.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchSongsAsync("test query", 20);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-song-123456", result[0].Id);
        Assert.Equal("Test Song", result[0].Title);
        Assert.Equal("Test Artist", result[0].Artist);
        Assert.Equal("Test Album", result[0].Album);
        Assert.Equal(180, result[0].Duration);
        Assert.Equal("TESTISRC1234", result[0].Isrc);
        Assert.Equal("https://example.com/cover-xl.jpg", result[0].CoverArtUrlLarge);
        Assert.False(result[0].IsLocal);
        Assert.Equal("deezer", result[0].ExternalProvider);
    }

    [Fact]
    public async Task SearchSongsAsync_AmpersandVariant_PreservesProviderOrderAndDeduplicates()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            requests.Add(pathAndQuery);

            var response = pathAndQuery.Contains("q=love%20and%20hyperbole", StringComparison.Ordinal)
                ? new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(2, "Shared Result"),
                        CreateTrackSearchResult(3, "Variant Result")
                    }
                }
                : new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(1, "Original Result"),
                        CreateTrackSearchResult(2, "Shared Result")
                    }
                };

            return CreateJsonResponse(JsonSerializer.Serialize(response));
        });

        var result = await _service.SearchSongsAsync("love & hyperbole", 3);

        Assert.Equal(["Original Result", "Shared Result", "Variant Result"], result.Select(song => song.Title));
        Assert.Equal(2, requests.Count);
        Assert.Contains("q=love%20%26%20hyperbole&limit=3&order=RANKING", requests[0]);
        Assert.Contains("q=love%20and%20hyperbole&limit=3&order=RANKING", requests[1]);
    }

    [Fact]
    public async Task SearchAlbumsAsync_ReturnsListOfAlbums()
    {
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 456789,
                    title = "Test Album",
                    nb_tracks = 12,
                    release_date = "2023-01-15",
                    cover_medium = "https://example.com/album.jpg",
                    cover_xl = "https://example.com/album-xl.jpg",
                    artist = new { id = 123, name = "Test Artist" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchAlbumsAsync("test album", 20);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-album-456789", result[0].Id);
        Assert.Equal("Test Album", result[0].Title);
        Assert.Equal("2023-01-15", result[0].ReleaseDate);
        Assert.Equal("Test Artist", result[0].Artist);
        Assert.Equal(12, result[0].SongCount);
        Assert.Equal(2023, result[0].Year);
        Assert.Equal("https://example.com/album-xl.jpg", result[0].CoverArtUrl);
        Assert.False(result[0].IsLocal);
    }

    [Fact]
    public async Task SearchArtistsAsync_ReturnsListOfArtists()
    {
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 789012,
                    name = "Test Artist",
                    nb_album = 5,
                    picture_medium = "https://example.com/artist.jpg",
                    picture_xl = "https://example.com/artist-xl.jpg"
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchArtistsAsync("test artist", 20);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ext-deezer-artist-789012", result[0].Id);
        Assert.Equal("Test Artist", result[0].Name);
        Assert.Equal(5, result[0].AlbumCount);
        Assert.Equal("https://example.com/artist-xl.jpg", result[0].ImageUrl);
        Assert.False(result[0].IsLocal);
    }

    [Theory]
    [InlineData("songs")]
    [InlineData("albums")]
    [InlineData("artists")]
    public async Task MetadataSearch_PropagatesCallerCancellation(string resultKind)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SearchCountAsync(resultKind, cancellation.Token));
    }

    [Fact]
    public async Task SearchBuckets_ReturnAllTypes()
    {
        // This test would need multiple HTTP calls mocked, simplified for now
        var emptyResponse = JsonSerializer.Serialize(new { data = Array.Empty<object>() });
        SetupHttpResponse(emptyResponse);

        var result = new allstarr.Models.Search.SearchResult
        {
            Songs = await _service.SearchSongsAsync("test"),
            Albums = await _service.SearchAlbumsAsync("test"),
            Artists = await _service.SearchArtistsAsync("test")
        };

        Assert.NotNull(result);
        Assert.NotNull(result.Songs);
        Assert.NotNull(result.Albums);
        Assert.NotNull(result.Artists);
    }

    private async Task<int> SearchCountAsync(string resultKind, CancellationToken cancellationToken) =>
        resultKind switch
        {
            "songs" => (await _service.SearchSongsAsync("query", cancellationToken: cancellationToken)).Count,
            "albums" => (await _service.SearchAlbumsAsync("query", cancellationToken: cancellationToken)).Count,
            "artists" => (await _service.SearchArtistsAsync("query", cancellationToken: cancellationToken)).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(resultKind), resultKind, null)
        };

    [Fact]
    public async Task SearchBuckets_AmpersandQuery_UseVariantsForEachRequestedBucket()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            lock (requests)
            {
                requests.Add(request.RequestUri!.PathAndQuery);
            }

            return CreateJsonResponse(JsonSerializer.Serialize(new { data = Array.Empty<object>() }));
        });

        await Task.WhenAll(
            _service.SearchSongsAsync("love & hyperbole", 1),
            _service.SearchAlbumsAsync("love & hyperbole", 1),
            _service.SearchArtistsAsync("love & hyperbole", 1));

        Assert.Contains(requests, request =>
            request.Contains("/search/track?q=love%20%26%20hyperbole&limit=1", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Contains("/search/track?q=love%20and%20hyperbole&limit=1", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Contains("/search/album?q=love%20%26%20hyperbole&limit=1", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Contains("/search/album?q=love%20and%20hyperbole&limit=1", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Contains("/search/artist?q=love%20%26%20hyperbole&limit=1", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Contains("/search/artist?q=love%20and%20hyperbole&limit=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindSongByIsrcAsync_UsesExactTrackEndpoint()
    {
        var requests = new List<(HttpMethod Method, string Uri, string[] Headers)>();
        SetupHttpResponse(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.Select(header => header.Key).ToArray()));

            return CreateJsonResponse(JsonSerializer.Serialize(new
            {
                id = 116348632,
                title = "Hey Jude",
                isrc = "GBUM71505902",
                duration = 429,
                track_position = 21,
                disk_number = 1,
                artist = new { id = 1, name = "The Beatles" },
                album = new
                {
                    id = 12047956,
                    title = "1",
                    cover_medium = "https://example.com/cover.jpg"
                }
            }));
        });

        var result = await _service.FindSongByIsrcAsync(" GBUM71505902 ");

        Assert.NotNull(result);
        Assert.Equal("ext-deezer-song-116348632", result.Id);
        Assert.Equal("GBUM71505902", result.Isrc);
        Assert.Equal("Hey Jude", result.Title);
        Assert.Equal("The Beatles", result.Artist);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.deezer.com/track/isrc:GBUM71505902", request.Uri);
        Assert.Empty(request.Headers);
    }

    [Theory]
    [InlineData("GBUM71505903")]
    [InlineData(null)]
    public async Task FindSongByIsrcAsync_RejectsMismatchedOrMissingRecordingCode(string? returnedIsrc)
    {
        SetupHttpResponse(JsonSerializer.Serialize(new
        {
            id = 116348632,
            title = "Hey Jude",
            isrc = returnedIsrc,
            duration = 429,
            artist = new { id = 1, name = "The Beatles" },
            album = new { id = 12047956, title = "1" }
        }));

        var result = await _service.FindSongByIsrcAsync("GBUM71505902");

        Assert.Null(result);
        _httpMessageHandlerMock.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetSongAsync_WithDeezerProvider_ReturnsSong()
    {
        var deezerResponse = new
        {
            id = 123456,
            title = "Test Song",
            duration = 200,
            track_position = 3,
            artist = new { id = 789, name = "Test Artist" },
            album = new { id = 456, title = "Test Album", cover_medium = "https://example.com/cover.jpg" }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetSongAsync("deezer", "123456");

        Assert.NotNull(result);
        Assert.Equal("ext-deezer-song-123456", result.Id);
        Assert.Equal("Test Song", result.Title);
    }

    [Fact]
    public async Task GetSongAsync_WithNonDeezerProvider_ReturnsNull()
    {
        var result = await _service.GetSongAsync("spotify", "123456");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSongAsync_RetainsEmbeddedAlbumFactsAndIgnoresUnspecifiedGainUnits()
    {
        SetupHttpResponse("""
            {"id":123456,"title":"Song","duration":200,"track_position":2,"disk_number":1,"gain":-8.2,
             "explicit_content_lyrics":0,"artist":{"id":1,"name":"Artist"},
             "contributors":[{"id":1,"name":"Artist"},{"id":2,"name":"Guest"}],
             "album":{"id":456,"title":"Album","artist":{"id":1,"name":"Artist"},"nb_tracks":10,"nb_disk":2,
               "upc":"0123456789012","label":"Label","copyright":"Copyright","description":"Album notes",
               "genres":{"data":[{"id":1,"name":"Pop"},{"id":2,"name":"Rock"}]},
               "cover_medium":"https://cdn.example.test/medium.jpg","cover_big":"https://cdn.example.test/big.jpg",
               "cover_xl":"https://cdn.example.test/xl.jpg"}}
            """);

        var song = await _service.GetSongAsync("deezer", "123456");

        Assert.NotNull(song);
        Assert.Equal(["Artist", "Guest"], song.Artists);
        Assert.Equal(10, song.TotalTracks);
        Assert.Equal(2, song.TotalDiscs);
        Assert.Equal("0123456789012", song.Upc);
        Assert.Equal("Label", song.Label);
        Assert.Equal("Copyright", song.Copyright);
        Assert.Equal("Album notes", song.Notes);
        Assert.Equal(["Pop", "Rock"], song.Genres);
        Assert.Equal("Pop", song.Genre);
        Assert.Equal(0, song.ExplicitContentLyrics);
        Assert.Equal("https://cdn.example.test/xl.jpg", song.CoverArtUrlLarge);
        Assert.Null(song.ReplayGain);
    }

    [Fact]
    public async Task SearchSongsAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        SetupHttpResponse(JsonSerializer.Serialize(new { data = Array.Empty<object>() }));

        var result = await _service.SearchSongsAsync("nonexistent", 20);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchTracks_WithExhaustedHttpRetries_ReturnsTypedTransientFailure()
    {
        SetupHttpResponse("Error", HttpStatusCode.InternalServerError);

        var result = await _service.SearchTracksAsync(PublicContext(), new("test", new(20)));

        Assert.Equal(ProviderErrorKind.TransientFailure, result.Error!.Kind);
        _httpMessageHandlerMock.Protected().Verify("SendAsync", Times.Exactly(4),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SearchTracks_WithExhaustedApiQuota_ReturnsTypedRateLimit()
    {
        SetupHttpResponse("""{"error":{"code":4,"message":"safe fixture"}}""");

        var result = await _service.SearchTracksAsync(PublicContext(), new("test", new(20)));

        Assert.Equal(ProviderErrorKind.RateLimited, result.Error!.Kind);
        _httpMessageHandlerMock.Protected().Verify("SendAsync", Times.Exactly(4),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task TypedMetadata_UsesTheSameCatalogParserWithoutAnAccount()
    {
        SetupHttpResponse(JsonSerializer.Serialize(new { data = new[] { CreateTrackSearchResult(42, "Track") } }));

        var result = await _service.SearchTracksAsync(PublicContext(), new("track", new(1)));

        var track = Assert.Single(result.RequireValue().Items);
        Assert.Equal("deezer", track.Id.ProviderId);
        Assert.Equal("42", track.Id.Value);
        Assert.Equal("Track", track.Title);
    }

    private static ProviderExecutionContext PublicContext() => new(
        new(ProviderActorKind.PublicRead, null), "deezer", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
            ProviderExplicitContentPolicy.Allow, false, false, false, ["deezer"]),
        "catalog-test", "catalog-fixture", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);


    [Theory]
    [InlineData("\"explicit_lyrics\":true,", true)]
    [InlineData("\"explicit_lyrics\":false,", false)]
    [InlineData("\"explicit_lyrics\":null,", null)]
    [InlineData("", null)]
    public async Task Album_metadata_preserves_source_release_evidence(string rating, bool? expectedExplicit)
    {
        var albumJson = $$$"""
            {"id":456789,"title":"Album","artist":{"id":123,"name":"Artist"},{{{rating}}}
              "upc":"0123456789012","edition":"Deluxe","nb_tracks":1,"release_date":"2026-09-04",
              "tracks":{"data":[{"id":111,"title":"Song","duration":180,"explicit_lyrics":true,"explicit_content_lyrics":1,
                "artist":{"id":123,"name":"Artist"},"album":{"id":456789,"title":"Album"}}]}}
            """;
        SetupHttpResponse(request => CreateJsonResponse(request.RequestUri!.AbsolutePath.Contains("/search/album", StringComparison.Ordinal)
            ? $$"""{"data":[{{albumJson}}]}""" : albumJson));

        var legacy = await _service.GetAlbumAsync("deezer", "456789");
        var searched = Assert.Single((await _service.SearchAlbumsAsync(PublicContext(), new("album", new(1)))).RequireValue().Items);
        var full = (await _service.GetAlbumAsync(PublicContext(), new(new("deezer", ProviderResourceKind.Album, "456789")))).RequireValue();

        Assert.NotNull(legacy);
        Assert.Equal(expectedExplicit, legacy.IsExplicit);
        Assert.Equal("0123456789012", legacy.Upc);
        Assert.Equal("Deluxe", legacy.Edition);
        foreach (var album in new[] { searched, full })
        {
            Assert.Equal(expectedExplicit, album.IsExplicit);
            Assert.Equal("0123456789012", album.Upc);
            Assert.Equal("Deluxe", album.Edition);
        }
        Assert.True(Assert.Single(full.Tracks).IsExplicit);
    }

    [Fact]
    public async Task GetAlbumAsync_WithDeezerProvider_ReturnsAlbumWithTracks()
    {
        var deezerResponse = new
        {
            id = 456789,
            title = "Test Album",
            nb_tracks = 2,
            release_date = "2023-05-20",
            cover_medium = "https://example.com/album.jpg",
            artist = new { id = 123, name = "Test Artist" },
            tracks = new
            {
                data = new[]
                {
                    new
                    {
                        id = 111,
                        title = "Track 1",
                        duration = 180,
                        track_position = 1,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 222,
                        title = "Track 2",
                        duration = 200,
                        track_position = 2,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetAlbumAsync("deezer", "456789");

        Assert.NotNull(result);
        Assert.Equal("ext-deezer-album-456789", result.Id);
        Assert.Equal("Test Album", result.Title);
        Assert.Equal("2023-05-20", result.ReleaseDate);
        Assert.Equal("Test Artist", result.Artist);
        Assert.Equal(2, result.Songs.Count);
        Assert.Equal("Track 1", result.Songs[0].Title);
        Assert.Equal("Track 2", result.Songs[1].Title);
    }

    [Fact]
    public async Task GetAlbumAsync_KeepsCompleteEmbeddedTracksWhenIdentifierEnrichmentIsUnavailable()
    {
        SetupHttpResponse(request => request.RequestUri!.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : CreateJsonResponse("""{"id":456789,"title":"PRIMA","nb_tracks":1,"artist":{"id":123,"name":"ADÉLA"},"tracks":{"data":[{"id":111,"title":"KGB","duration":180,"artist":{"id":123,"name":"ADÉLA"}}]}}"""));

        var result = await _service.GetAlbumAsync("deezer", "456789");

        Assert.Equal("KGB", Assert.Single(result!.Songs).Title);
    }

    [Fact]
    public async Task GetAlbumAsync_EnrichesCompleteEmbeddedTracksWithRecordingIdentifiers()
    {
        SetupHttpResponse(request => CreateJsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal)
            ? """{"data":[{"id":111,"title":"KGB","duration":180,"isrc":"USUM72601922","artist":{"id":123,"name":"ADÉLA"}}]}"""
            : """{"id":456789,"title":"PRIMA","nb_tracks":1,"artist":{"id":123,"name":"ADÉLA"},"tracks":{"data":[{"id":111,"title":"KGB","duration":180,"artist":{"id":123,"name":"ADÉLA"}}]}}"""));

        var result = await _service.GetAlbumAsync("deezer", "456789");

        Assert.Equal("USUM72601922", Assert.Single(result!.Songs).Isrc);
    }

    [Fact]
    public async Task GetAlbumAsync_WithNonDeezerProvider_ReturnsNull()
    {
        var result = await _service.GetAlbumAsync("spotify", "123456");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAlbumAsync_PaginatesTracklistWhenAlbumDetailIsPartial()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            requests.Add(pathAndQuery);

            if (pathAndQuery.Contains("index=1", StringComparison.Ordinal))
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(222, "Track 2")
                    }
                }));
            }

            if (pathAndQuery.Contains("/tracks", StringComparison.Ordinal))
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(111, "Track 1")
                    },
                    next = "https://api.deezer.com/album/456789/tracks?limit=100&index=1"
                }));
            }

            return CreateJsonResponse(JsonSerializer.Serialize(new
            {
                id = 456789,
                title = "Paged Album",
                nb_tracks = 2,
                artist = new { id = 123, name = "Test Artist" },
                tracks = new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(111, "Track 1")
                    }
                }
            }));
        });

        var result = await _service.GetAlbumAsync("deezer", "456789");

        Assert.NotNull(result);
        Assert.Equal(["Track 1", "Track 2"], result.Songs.Select(song => song.Title));
        Assert.Contains("/album/456789/tracks?index=0&limit=100", requests);
        Assert.Contains("/album/456789/tracks?limit=100&index=1", requests);
    }

    [Fact]
    public async Task GetArtistAlbumsAsync_FallsBackToDocumentedIndexPagination()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            requests.Add(pathAndQuery);

            if (pathAndQuery == "/artist/27")
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    id = 27,
                    name = "Artist",
                    nb_album = 2
                }));
            }

            if (pathAndQuery.Contains("index=1", StringComparison.Ordinal))
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = 2002,
                            title = "Second Album",
                            nb_tracks = 8
                        }
                    }
                }));
            }

            return CreateJsonResponse(JsonSerializer.Serialize(new
            {
                data = new object[]
                {
                    new
                    {
                        id = 2001,
                        title = "First Album",
                        nb_tracks = 10
                    }
                },
                total = 2
            }));
        });

        var result = await _service.GetArtistAlbumsAsync("deezer", "27");

        Assert.Equal(["First Album", "Second Album"], result.Select(album => album.Title));
        Assert.All(result, album =>
        {
            Assert.Equal("Artist", album.Artist);
            Assert.Equal("ext-deezer-artist-27", album.ArtistId);
        });
        Assert.Contains("/artist/27/albums?index=0&limit=100", requests);
        Assert.Contains("/artist/27/albums?index=1&limit=100", requests);
        Assert.Contains("/artist/27", requests);
    }

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    private void SetupHttpResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
                Task.FromResult(responseFactory(request)));
    }

    private static HttpResponseMessage CreateJsonResponse(string content)
    {
        return new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
    }

    private static object CreateTrackSearchResult(long id, string title)
    {
        return new
        {
            id,
            title,
            duration = 180,
            artist = new { id = 789, name = "Test Artist" },
            album = new { id = 456, title = "Test Album", cover_medium = "https://example.com/cover.jpg" }
        };
    }

    private static object CreatePlaylistSearchResult(long id, string title)
    {
        return new
        {
            id,
            title,
            nb_tracks = 10,
            picture_medium = "https://example.com/playlist.jpg",
            user = new { name = "Playlist User" }
        };
    }


    [Fact]
    public async Task SearchSongsAsync_ReturnsAllExplicitFlagsForViewerPolicy()
    {
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Explicit Original",
                    duration = 180,
                    explicit_content_lyrics = 1,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 2,
                    title = "Clean Version",
                    duration = 180,
                    explicit_content_lyrics = 3,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                },
                new
                {
                    id = 3,
                    title = "Naturally Clean",
                    duration = 180,
                    explicit_content_lyrics = 0,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchSongsAsync("test", 20);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task SearchSongsAsync_PreservesUnknownExplicitInfo()
    {
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "No Explicit Info",
                    duration = 180,
                    // No explicit_content_lyrics field
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchSongsAsync("test", 20);

        Assert.Single(result);
        Assert.Equal("No Explicit Info", result[0].Title);
        Assert.Null(result[0].ExplicitContentLyrics);
    }

    [Fact]
    public async Task GetAlbumAsync_ReturnsAllExplicitFlagsForViewerPolicy()
    {
        var deezerResponse = new
        {
            id = 456789,
            title = "Test Album",
            nb_tracks = 3,
            release_date = "2023-05-20",
            cover_medium = "https://example.com/album.jpg",
            artist = new { id = 123, name = "Test Artist" },
            tracks = new
            {
                data = new object[]
                {
                    new
                    {
                        id = 111,
                        title = "Explicit Track",
                        duration = 180,
                        explicit_content_lyrics = 1,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 222,
                        title = "Clean Version Track",
                        duration = 200,
                        explicit_content_lyrics = 3,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    },
                    new
                    {
                        id = 333,
                        title = "Naturally Clean Track",
                        duration = 220,
                        explicit_content_lyrics = 0,
                        artist = new { id = 123, name = "Test Artist" },
                        album = new { id = 456789, title = "Test Album", cover_medium = "https://example.com/album.jpg" }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetAlbumAsync("deezer", "456789");

        Assert.NotNull(result);
        Assert.Equal(3, result.Songs.Count);
        Assert.Contains(result.Songs, s => s.Title == "Explicit Track");
        Assert.Contains(result.Songs, s => s.Title == "Naturally Clean Track");
        Assert.Contains(result.Songs, s => s.Title == "Clean Version Track");
        Assert.Equal(new int?[] { 1, 3, 0 }, result.Songs.Select(song => song.ExplicitContentLyrics));
    }

    [Fact]
    public async Task SearchSongsAsync_ParsesExplicitContentLyrics()
    {
        var deezerResponse = new
        {
            data = new object[]
            {
                new
                {
                    id = 1,
                    title = "Test Track",
                    duration = 180,
                    explicit_content_lyrics = 1,
                    artist = new { id = 100, name = "Artist" },
                    album = new { id = 200, title = "Album", cover_medium = "https://example.com/cover.jpg" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchSongsAsync("test", 20);

        Assert.Single(result);
        Assert.Equal(1, result[0].ExplicitContentLyrics);
    }



    [Fact]
    public async Task SearchPlaylistsAsync_ReturnsListOfPlaylists()
    {
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 12345,
                    title = "Chill Vibes",
                    nb_tracks = 50,
                    picture_medium = "https://example.com/playlist1.jpg",
                    user = new { name = "Test User" }
                },
                new
                {
                    id = 67890,
                    title = "Workout Mix",
                    nb_tracks = 30,
                    picture_medium = "https://example.com/playlist2.jpg",
                    user = new { name = "Gym Buddy" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchPlaylistsAsync("chill");

        Assert.Equal(2, result.Count);
        Assert.Equal("Chill Vibes", result[0].Name);
        Assert.Equal(50, result[0].TrackCount);
        Assert.Equal("ext-deezer-playlist-12345", result[0].Id);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_AmpersandVariant_PreservesProviderOrderAndDeduplicates()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            requests.Add(pathAndQuery);

            var response = pathAndQuery.Contains("q=love%20and%20hyperbole", StringComparison.Ordinal)
                ? new
                {
                    data = new object[]
                    {
                        CreatePlaylistSearchResult(2, "Shared Playlist"),
                        CreatePlaylistSearchResult(3, "Variant Playlist")
                    }
                }
                : new
                {
                    data = new object[]
                    {
                        CreatePlaylistSearchResult(1, "Original Playlist"),
                        CreatePlaylistSearchResult(2, "Shared Playlist")
                    }
                };

            return CreateJsonResponse(JsonSerializer.Serialize(response));
        });

        var result = await _service.SearchPlaylistsAsync("love & hyperbole", 3);

        Assert.Equal(["Original Playlist", "Shared Playlist", "Variant Playlist"], result.Select(playlist => playlist.Name));
        Assert.Equal(2, requests.Count);
        Assert.Contains("q=love%20%26%20hyperbole&limit=3&order=RANKING", requests[0]);
        Assert.Contains("q=love%20and%20hyperbole&limit=3&order=RANKING", requests[1]);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_WithLimit_RespectsLimit()
    {
        var deezerResponse = new
        {
            data = new[]
            {
                new
                {
                    id = 12345,
                    title = "Playlist 1",
                    nb_tracks = 10,
                    picture_medium = "https://example.com/p1.jpg",
                    user = new { name = "User 1" }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchPlaylistsAsync("test", 1);

        Assert.Single(result);
    }

    [Fact]
    public async Task SearchPlaylistsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        var deezerResponse = new
        {
            data = new object[] { }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.SearchPlaylistsAsync("nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPlaylistAsync_WithValidId_ReturnsPlaylist()
    {
        var deezerResponse = new
        {
            id = 12345,
            title = "Best Of Jazz",
            description = "The best jazz tracks",
            nb_tracks = 100,
            picture_medium = "https://example.com/jazz.jpg",
            user = new { name = "Jazz Lover" }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetPlaylistAsync("deezer", "12345");

        Assert.NotNull(result);
        Assert.Equal("Best Of Jazz", result.Name);
        Assert.Equal(100, result.TrackCount);
        Assert.Equal("ext-deezer-playlist-12345", result.Id);
    }

    [Fact]
    public async Task GetPlaylistAsync_WithWrongProvider_ReturnsNull()
    {
        var result = await _service.GetPlaylistAsync("qobuz", "12345");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_ReturnsListOfSongs()
    {
        var deezerResponse = new
        {
            tracks = new
            {
                data = new[]
                {
                    new
                    {
                        id = 111,
                        title = "Track 1",
                        isrc = "TESTISRC0001",
                        duration = 200,
                        track_position = 1,
                        disk_number = 1,
                        artist = new
                        {
                            id = 999,
                            name = "Artist A"
                        },
                        album = new
                        {
                            id = 888,
                            title = "Album X",
                            release_date = "2020-01-15",
                            cover_medium = "https://example.com/cover.jpg"
                        }
                    },
                    new
                    {
                        id = 222,
                        title = "Track 2",
                        isrc = "TESTISRC0002",
                        duration = 180,
                        track_position = 2,
                        disk_number = 1,
                        artist = new
                        {
                            id = 777,
                            name = "Artist B"
                        },
                        album = new
                        {
                            id = 666,
                            title = "Album Y",
                            release_date = "2021-05-20",
                            cover_medium = "https://example.com/cover2.jpg"
                        }
                    }
                }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");

        Assert.Equal(2, result.Count);
        Assert.Equal("Track 1", result[0].Title);
        Assert.Equal("Artist A", result[0].Artist);
        Assert.Equal("ext-deezer-song-111", result[0].Id);
        Assert.Equal("TESTISRC0001", result[0].Isrc);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_WithWrongProvider_ReturnsEmptyList()
    {
        var result = await _service.GetPlaylistTracksAsync("qobuz", "12345");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_PaginatesTracklistWhenPlaylistDetailIsPartial()
    {
        var requests = new List<string>();
        SetupHttpResponse(request =>
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            requests.Add(pathAndQuery);

            if (pathAndQuery.Contains("index=1", StringComparison.Ordinal))
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(222, "Track 2")
                    }
                }));
            }

            if (pathAndQuery.Contains("/tracks", StringComparison.Ordinal))
            {
                return CreateJsonResponse(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(111, "Track 1")
                    },
                    next = "https://api.deezer.com/playlist/12345/tracks?limit=100&index=1"
                }));
            }

            return CreateJsonResponse(JsonSerializer.Serialize(new
            {
                id = 12345,
                title = "Paged Playlist",
                nb_tracks = 2,
                tracks = new
                {
                    data = new object[]
                    {
                        CreateTrackSearchResult(111, "Track 1")
                    }
                }
            }));
        });

        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");

        Assert.Equal(["Track 1", "Track 2"], result.Select(song => song.Title));
        Assert.All(result, song => Assert.Equal("Paged Playlist", song.Album));
        Assert.Equal(1, result[0].Track);
        Assert.Equal(2, result[1].Track);
        Assert.Contains("/playlist/12345/tracks?index=0&limit=100", requests);
        Assert.Contains("/playlist/12345/tracks?limit=100&index=1", requests);
    }

    [Fact]
    public async Task GetPlaylistTracksAsync_WithEmptyPlaylist_ReturnsEmptyList()
    {
        var deezerResponse = new
        {
            tracks = new
            {
                data = new object[] { }
            }
        };

        SetupHttpResponse(JsonSerializer.Serialize(deezerResponse));

        var result = await _service.GetPlaylistTracksAsync("deezer", "12345");

        Assert.Empty(result);
    }

}
