using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using Microsoft.Extensions.Logging;

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
    public void PersistedQueries_HaveExplicitVersionedDefinitions()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Providers", "Spotify", "SpotifyPathfinderPlaylistClient.cs"));

        Assert.Contains("PathfinderOperationDefinition LibraryQuery", source, StringComparison.Ordinal);
        Assert.Contains("PathfinderOperationDefinition PlaylistQuery", source, StringComparison.Ordinal);
        Assert.Contains("new(LibraryOperation, LibraryQueryHash, 1)", source, StringComparison.Ordinal);
        Assert.Contains("new(PlaylistOperation, PlaylistQueryHash, 1)", source, StringComparison.Ordinal);
        Assert.Contains("version = operation.Version", source, StringComparison.Ordinal);
        Assert.Contains("sha256Hash = operation.Sha256Hash", source, StringComparison.Ordinal);
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
        using var http = new HttpClient(new JsonHandler(payload));
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
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
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

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
