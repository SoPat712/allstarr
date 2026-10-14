using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using Microsoft.Extensions.Logging;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public sealed class SpotifyPathfinderCompatibilityTests
{
    [Fact]
    public async Task StalePersistedQuery_ReturnsActionableHostError()
    {
        using var http = new HttpClient(new JsonHandler(
            """{"errors":[{"message":"PersistedQueryNotFound","extensions":{"code":"PERSISTED_QUERY_NOT_FOUND"}}]}"""));
        var client = new SpotifyPathfinderPlaylistClient(http);

        var outcome = await client.GetUserPlaylistsAsync(
            "token",
            new ProviderPageRequest(20),
            null,
            CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, outcome.Error!.Kind);
        Assert.Equal("provider-contract-changed", outcome.Error.Code);
        Assert.Contains("Update Allstarr", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryQuery_SendsVersionedPersistedQueryDefinition()
    {
        var handler = new JsonHandler("""{"data":{"me":{"libraryV3":{"items":[],"totalCount":0}}}}""");
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(http);

        var outcome = await client.GetUserPlaylistsAsync(
            "token",
            new ProviderPageRequest(20),
            null,
            CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        AssertOperation(handler.LastRequestUri, "libraryV3", SpotifyPathfinderPlaylistClient.LibraryQueryHash);
        var query = Query(handler.LastRequestUri);
        using var variables = JsonDocument.Parse(query["variables"]);
        Assert.Equal(["Playlists"], variables.RootElement.GetProperty("filters")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("", variables.RootElement.GetProperty("textFilter").GetString());
    }

    [Fact]
    public async Task Complete_thirty_row_playlist_preserves_position_four_identity_and_metadata()
    {
        var messages = new List<string>();
        var items = Enumerable.Range(0, 30).Select(position => new
        {
            itemV2 = new
            {
                data = new
                {
                    uri = $"spotify:track:track-{position}",
                    name = position == 4 ? "Synthetic replacement" : $"Track {position}",
                    artists = new
                    {
                        items = new[]
                        {
                            new
                            {
                                uri = $"spotify:artist:artist-{position}",
                                profile = new { name = position == 4 ? "Synthetic artist" : "Artist" }
                            }
                        }
                    },
                    albumOfTrack = new
                    {
                        uri = $"spotify:album:album-{position}",
                        name = position == 4 ? "Synthetic album" : "Album"
                    },
                    trackDuration = new { totalMilliseconds = 180_000 + position },
                    contentRating = new { label = "NONE" }
                }
            }
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                playlistV2 = new
                {
                    name = "Synthetic personalized playlist",
                    ownerV2 = new { data = new { username = "owner" } },
                    revisionId = "unchanged-revision",
                    content = new { totalCount = 30, items }
                }
            }
        });
        var handler = new JsonHandler(payload);
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(
            http,
            new CollectingLogger<SpotifyPathfinderPlaylistClient>(messages));

        var outcome = await client.GetPlaylistTracksAsync(
            "token",
            new ProviderPlaylistTracksRequest(
                new("spotify", ProviderResourceKind.Playlist, "playlist"),
                new ProviderPageRequest(30)),
            CancellationToken.None,
            "account-fingerprint");

        Assert.True(outcome.IsSuccess);
        var tracks = outcome.RequireValue().Tracks.Items;
        Assert.Equal(30, tracks.Count);
        Assert.Equal(Enumerable.Range(0, 30), tracks.Select(item => item.Position));
        var replacement = tracks[4];
        Assert.Equal("track-4", replacement.TrackId.Value);
        Assert.Equal("Synthetic replacement", replacement.Metadata!.Title);
        Assert.Equal("Synthetic artist", Assert.Single(replacement.Metadata.Artists).Name);
        Assert.Equal("Synthetic album", replacement.Metadata.AlbumTitle);
        Assert.Equal(TimeSpan.FromMilliseconds(180_004), replacement.Metadata.Duration);
        var completion = Assert.Single(messages, item =>
            item.Contains("Spotify Pathfinder page completed", StringComparison.Ordinal));
        Assert.Contains("DeclaredCount: 30", completion);
        Assert.Contains("RawCount: 30", completion);
        Assert.Contains("MappedCount: 30", completion);
        Assert.DoesNotContain("Synthetic replacement", completion);
        Assert.DoesNotContain("track-4", completion);
        Assert.DoesNotContain("token", completion, StringComparison.OrdinalIgnoreCase);
        AssertOperation(handler.LastRequestUri, "fetchPlaylist", SpotifyPathfinderPlaylistClient.PlaylistQueryHash);
    }

    [Theory]
    [InlineData("3fCEBvwpzqBGeSiRq7yTs3")]
    [InlineData("spotify:playlist:3fCEBvwpzqBGeSiRq7yTs3")]
    [InlineData("https://open.spotify.com/playlist/3fCEBvwpzqBGeSiRq7yTs3?si=fixture#ignored")]
    public async Task PlaylistReferencesNormalizeBeforeTheProviderRequest(string reference)
    {
        var handler = new JsonHandler("""{"data":{"playlistV2":{"name":"Public mix","revisionId":"revision","content":{"totalCount":0,"items":[]}}}}""");
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(http);
        var outcome = await client.GetPlaylistTracksAsync("token", new(
            new("spotify", ProviderResourceKind.Playlist, reference), new ProviderPageRequest()), default);
        Assert.True(outcome.IsSuccess);
        using var variables = JsonDocument.Parse(Query(handler.LastRequestUri)["variables"]);
        Assert.Equal("spotify:playlist:3fCEBvwpzqBGeSiRq7yTs3", variables.RootElement.GetProperty("uri").GetString());
        Assert.Equal("3fCEBvwpzqBGeSiRq7yTs3", outcome.RequireValue().Playlist.Id.Value);
    }

    [Theory]
    [InlineData("https://example.test/playlist/3fCEBvwpzqBGeSiRq7yTs3")]
    [InlineData("spotify:track:3fCEBvwpzqBGeSiRq7yTs3")]
    [InlineData("not a playlist")]
    public async Task InvalidPlaylistReferencesFailBeforeSendingCredentials(string reference)
    {
        var handler = new JsonHandler("{}");
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(http);
        var outcome = await client.GetPlaylistTracksAsync("token", new(
            new("spotify", ProviderResourceKind.Playlist, reference), new ProviderPageRequest()), default);
        Assert.False(outcome.IsSuccess);
        Assert.Null(handler.LastRequestUri);
        Assert.Equal(ProviderErrorKind.PermanentFailure, outcome.Error!.Kind);
    }

    [Fact]
    public async Task LikedSongsUsesLibraryTracksAndPreservesGlobalSourcePositions()
    {
        var handler = new JsonHandler("""
            {"data":{"me":{"library":{"tracks":{"totalCount":4,"items":[
              {"addedAt":{"isoString":"2026-10-13T00:00:00Z"},"itemV2":{"data":{"uri":"spotify:track:first","name":"First","artists":{"items":[{"profile":{"name":"Artist"}}]},"trackDuration":{"totalMilliseconds":180000}}}},
              {"addedAt":{"isoString":"2026-10-12T00:00:00Z"},"itemV2":{"data":{"uri":"spotify:track:second","name":"Second","artists":{"items":[{"profile":{"name":"Artist"}}]},"trackDuration":{"totalMilliseconds":190000}}}}
            ]}}}}}
            """);
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(http);
        var outcome = await client.GetPlaylistTracksAsync("token", new(
            new("spotify", ProviderResourceKind.Playlist, "liked-songs"), new ProviderPageRequest(2, "1")), default, "account-fingerprint");
        Assert.True(outcome.IsSuccess);
        var page = outcome.RequireValue();
        Assert.Equal("Liked Songs", page.Playlist.Name);
        Assert.Equal(4, page.Playlist.TrackCount);
        Assert.Equal([1, 2], page.Tracks.Items.Select(item => item.Position));
        Assert.Equal(["first", "second"], page.Tracks.Items.Select(item => item.TrackId.Value));
        Assert.Equal("3", page.Tracks.NextCursor);
        AssertOperation(handler.LastRequestUri, "fetchLibraryTracks", "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240");
        using var variables = JsonDocument.Parse(Query(handler.LastRequestUri)["variables"]);
        Assert.Equal(1, variables.RootElement.GetProperty("offset").GetInt32());
        Assert.False(variables.RootElement.TryGetProperty("uri", out _));
    }

    [Fact]
    public async Task ConfiguredQueriesUseTheSelectedEndpointAndObserveLiveHashChanges()
    {
        var handler = new JsonHandler("""{"data":{"me":{"libraryV3":{"items":[],"totalCount":0}}}}""");
        using var http = new HttpClient(handler);
        var settings = new SpotifyApiSettings { PathfinderEndpoint = "https://api-partner.spotify.com/pathfinder/v2/query", LibraryQueryHash = new string('b', 64) };
        var client = new SpotifyPathfinderPlaylistClient(http, null, null, settings);
        Assert.True((await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default)).IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(settings.PathfinderEndpoint, handler.LastRequestUri!.AbsoluteUri);
        using var first = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("libraryV3", first.RootElement.GetProperty("operationName").GetString());
        Assert.Equal(settings.LibraryQueryHash, first.RootElement.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
        settings.LibraryQueryHash = new string('c', 64);
        await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default);
        using var second = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(settings.LibraryQueryHash, second.RootElement.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
    }

    [Theory]
    [InlineData("https://example.test/query", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://api-partner.spotify.com/pathfinder/v1/query", "invalid")]
    public async Task InvalidCompatibilitySettingsNeverSendTheBearerToken(string endpoint, string hash)
    {
        var handler = new JsonHandler("{}");
        using var http = new HttpClient(handler);
        var client = new SpotifyPathfinderPlaylistClient(http, null, null,
            new SpotifyApiSettings { PathfinderEndpoint = endpoint, LibraryQueryHash = hash });
        var outcome = await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default);
        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error!.Kind);
        Assert.Null(handler.LastRequestUri);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CollectingLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }

    private static void AssertOperation(Uri? requestUri, string operationName, string hash)
    {
        var query = Query(requestUri);
        Assert.Equal(operationName, query["operationName"]);
        using var extensions = JsonDocument.Parse(query["extensions"]);
        var persisted = extensions.RootElement.GetProperty("persistedQuery");
        Assert.Equal(1, persisted.GetProperty("version").GetInt32());
        Assert.Equal(hash, persisted.GetProperty("sha256Hash").GetString());
    }

    private static Dictionary<string, string> Query(Uri? requestUri)
    {
        Assert.NotNull(requestUri);
        return requestUri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
    }
}
