using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class SpotifyPathfinderFixtureTests
{
    public static TheoryData<string, string> KnownPlaylists => new()
    {
        { "3fCEBvwpzqBGeSiRq7yTs3", "Saved playlist" },
        { "62U2t2VrRycoqlyf8ZT4T6", "Spotify generated playlist" },
        { "6TpGxGXKm0DKTDdFoRWV3R", "Shared playlist" }
    };

    [Theory]
    [MemberData(nameof(KnownPlaylists))]
    public async Task LibraryV3_DiscoversKnownPlaylistKindsWithArtwork(string playlistId, string name)
    {
        var handler = new FixtureHandler(playlistId, name);
        var client = new SpotifyPathfinderPlaylistClient(
            new HttpClient(handler),
            new TestMemoryApplicationCache());

        var outcome = await client.GetUserPlaylistsAsync(
            "account-access-token",
            new ProviderPageRequest(100),
            query: null,
            CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        var playlist = Assert.Single(outcome.RequireValue().Items);
        Assert.Equal(playlistId, playlist.Id.Value);
        Assert.Equal(name, playlist.Name);
        Assert.Equal(42, playlist.TrackCount);
        Assert.Equal("fixture-revision", playlist.SourceRevision);
        Assert.NotNull(playlist.Artwork);
        Assert.Null(outcome.RequireValue().NextCursor);
        var artwork = await client.GetPlaylistArtworkUriAsync(
            "account-access-token",
            playlist.Artwork!,
            CancellationToken.None);
        Assert.True(artwork.IsSuccess);
        Assert.Equal($"https://i.scdn.co/image/{playlistId}", artwork.RequireValue().ToString());
        Assert.Equal(1, handler.RequestCount);
        var decodedRequest = Uri.UnescapeDataString(handler.RequestUri);
        Assert.Contains("operationName=libraryV3", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("\"flatten\":true", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("\"expandedFolders\":[]", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("\"folderUri\":null", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("\"includeFoldersWhenFlattening\":true", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("\"withCuration\":true", decodedRequest, StringComparison.Ordinal);
        Assert.Contains("Bearer account-access-token", handler.Authorization);
    }

    private sealed class FixtureHandler(string playlistId, string name) : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri?.ToString() ?? string.Empty;
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            var playlist = new
            {
                uri = $"spotify:playlist:{playlistId}",
                name = new { transformedLabel = name },
                description = new { transformedLabel = "Fixture description" },
                ownerV2 = new
                {
                    data = new
                    {
                        username = "fixture-owner",
                        name = "Fixture owner"
                    }
                },
                totalCount = "42",
                revisionId = "fixture-revision",
                visuals = new
                {
                    avatarImage = new
                    {
                        sources = new[]
                        {
                            new
                            {
                                url = $"https://i.scdn.co/image/{playlistId}",
                                width = 640,
                                height = 640
                            }
                        }
                    }
                }
            };
            var payload = new
            {
                data = new
                {
                    me = new
                    {
                        libraryV3 = new
                        {
                            items = new[]
                            {
                                new
                                {
                                    item = new
                                    {
                                        data = new
                                        {
                                            __typename = "Folder",
                                            uri = "spotify:user:fixture:folder:shared",
                                            items = new[]
                                            {
                                                new
                                                {
                                                    item = new
                                                    {
                                                        data = playlist
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            },
                            totalCount = 1
                        }
                    }
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
