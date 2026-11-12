using allstarr.Core.Caching;
using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class SpotifyPlaylistArtworkTests
{
    [Theory]
    [InlineData("Release Radar", "newjams-images.scdn.co")]
    [InlineData("Daily Mix", "newjams-images.scdn.co")]
    [InlineData("Release Radar", "pickasso.spotifycdn.com")]
    public async Task Personalized_covers_take_precedence_over_larger_template_images(string name, string host)
    {
        var personalized = $"https://{host}/image/listener-cover";
        var handler = new ArtworkHandler
        {
            Name = name,
            ImageItems =
            [
                new { sources = new[] { new { url = "https://i.scdn.co/image/template", width = 1200 } } },
                new
                {
                    sources = new[]
                    {
                        new { url = "https://newjams-images.scdn.co/image/small-listener-cover", width = 300 },
                        new { url = personalized, width = 640 },
                        new { url = "https://untrusted.example/listener-cover", width = 2000 }
                    }
                }
            ]
        };
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), new TestMemoryApplicationCache());

        var uncached = await client.GetPlaylistArtworkUriAsync("token",
            new ProviderArtworkReference(TracksRequest().PlaylistId, revision: "unchanged-content"), default);
        var discovery = (await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default)).RequireValue();
        var playlist = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;

        Assert.Equal(personalized, uncached.RequireValue().AbsoluteUri);
        Assert.Equal(Assert.Single(discovery.Items).Artwork, playlist.Artwork);
        Assert.Equal(personalized,
            (await client.GetPlaylistArtworkUriAsync("token", playlist.Artwork!, default)).RequireValue().AbsoluteUri);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Spotify_owned_playlist_uses_the_public_thumbnail_when_no_response_image_is_offered()
    {
        const string fallback = "https://pickasso.spotifycdn.com/image/public-cover";
        var handler = new ArtworkHandler
        {
            OwnerUsername = "spotify",
            ThumbnailResponse = JsonSerializer.Serialize(new { thumbnail_url = fallback }),
            ImageItems = []
        };
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), new TestMemoryApplicationCache());

        var uncached = await client.GetPlaylistArtworkUriAsync("token",
            new ProviderArtworkReference(TracksRequest().PlaylistId, revision: "unchanged-content"), default);
        var playlist = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;
        var discovery = (await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default)).RequireValue();
        var artwork = await client.GetPlaylistArtworkUriAsync("token", playlist.Artwork!, default);

        Assert.Equal(fallback, uncached.RequireValue().AbsoluteUri);
        Assert.Equal(fallback, artwork.RequireValue().AbsoluteUri);
        Assert.Equal(Assert.Single(discovery.Items).Artwork, playlist.Artwork);
        Assert.Equal(1, handler.ThumbnailRequestCount);
        Assert.Null(handler.ThumbnailAuthorization);
        Assert.Equal("https://open.spotify.com/oembed?url=https%3A%2F%2Fopen.spotify.com%2Fplaylist%2Fpersonal-playlist",
            handler.ThumbnailRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Spotify_owned_playlist_keeps_the_account_response_cover_without_requesting_public_thumbnail()
    {
        var handler = new ArtworkHandler
        {
            OwnerUsername = "spotify",
            ThumbnailResponse = """{"thumbnail_url":"https://pickasso.spotifycdn.com/image/public-cover"}""",
            ImageItems = [new { sources = new[] { new { url = "https://pickasso.spotifycdn.com/image/account-cover", width = 640, height = 640 } } }]
        };
        var cache = new TestMemoryApplicationCache();
        foreach (var revision in new string?[] { null, "public-oembed-v1" })
            await cache.SetAsync(CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey(
                "spotify", TracksRequest().PlaylistId.Value, revision),
                new { Uri = "https://pickasso.spotifycdn.com/image/cached-public-cover" });
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), cache);
        var artwork = await client.GetPlaylistArtworkUriAsync("token", new ProviderArtworkReference(TracksRequest().PlaylistId), default);
        Assert.Equal("https://pickasso.spotifycdn.com/image/account-cover", artwork.RequireValue().AbsoluteUri);
        Assert.Equal(0, handler.ThumbnailRequestCount);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("malformed")]
    [InlineData("unsafe")]
    [InlineData("oversized")]
    public async Task Unusable_public_thumbnail_without_response_artwork_caches_the_miss(string response)
    {
        var handler = new ArtworkHandler
        {
            OwnerUsername = "spotify",
            ImageItems = [],
            ThumbnailStatus = response == "failure" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            ThumbnailResponse = response switch
            {
                "malformed" => "{",
                "oversized" => new string(' ', 65537),
                _ => "{\"thumbnail_url\":\"https://untrusted.example/cover\"}"
            }
        };
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), new TestMemoryApplicationCache());

        var first = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;
        var second = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;

        Assert.Null(first.Artwork);
        Assert.Null(second.Artwork);
        Assert.Equal("unchanged-content", second.SourceRevision);
        Assert.Equal(1, handler.ThumbnailRequestCount);
    }

    [Theory]
    [InlineData("artwork")]
    [InlineData("tracks")]
    [InlineData("discovery")]
    public async Task Account_covers_for_the_same_playlist_and_revision_do_not_share_cached_results(string read)
    {
        var cache = new TestMemoryApplicationCache();
        var firstHandler = new ArtworkHandler
        {
            OwnerUsername = "spotify",
            CoverUrl = "https://pickasso.spotifycdn.com/image/first-listener"
        };
        var secondHandler = new ArtworkHandler
        {
            OwnerUsername = "spotify",
            CoverUrl = "https://pickasso.spotifycdn.com/image/second-listener"
        };
        var first = new SpotifyPathfinderPlaylistClient(new HttpClient(firstHandler), cache);
        var second = new SpotifyPathfinderPlaylistClient(new HttpClient(secondHandler), cache);
        var reference = new ProviderArtworkReference(TracksRequest().PlaylistId, revision: "unchanged-content");
        switch (read)
        {
            case "tracks":
                await first.GetPlaylistTracksAsync("first-session", TracksRequest(), default);
                break;
            case "discovery":
                await first.GetUserPlaylistsAsync("first-session", new ProviderPageRequest(), null, default);
                break;
            default:
                await first.GetPlaylistArtworkUriAsync("first-session", reference, default);
                break;
        }

        var secondArtwork = await second.GetPlaylistArtworkUriAsync("second-session", reference, default);
        var firstArtwork = await first.GetPlaylistArtworkUriAsync("first-session", reference, default);

        Assert.Equal(secondHandler.CoverUrl, secondArtwork.RequireValue().AbsoluteUri);
        Assert.Equal(firstHandler.CoverUrl, firstArtwork.RequireValue().AbsoluteUri);
        Assert.Equal(1, firstHandler.RequestCount);
        Assert.Equal(1, secondHandler.RequestCount);
        Assert.Equal(0, firstHandler.ThumbnailRequestCount + secondHandler.ThumbnailRequestCount);
    }

    [Fact]
    public async Task Personalized_cover_without_dimensions_takes_precedence_over_a_template()
    {
        const string personalized = "https://newjams-images.scdn.co/image/listener-cover";
        var handler = new ArtworkHandler
        {
            ImageItems =
            [
                new { sources = new[] { new { url = "https://i.scdn.co/image/template", width = 1280 } } },
                new { sources = new[] { new { url = personalized } } }
            ]
        };
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler));

        var artwork = await client.GetPlaylistArtworkUriAsync("token",
            new ProviderArtworkReference(TracksRequest().PlaylistId), default);

        Assert.Equal(personalized, artwork.RequireValue().AbsoluteUri);
    }

    [Fact]
    public async Task Generic_covers_preserve_first_image_precedence_and_choose_its_largest_source()
    {
        var handler = new ArtworkHandler
        {
            ImageItems =
            [
                new
                {
                    sources = new[]
                    {
                        new { url = "https://i.scdn.co/image/small", width = 300 },
                        new { url = "https://i.scdn.co/image/large", width = 640 },
                        new { url = "http://newjams-images.scdn.co/image/insecure", width = 2000 }
                    }
                },
                new { sources = new[] { new { url = "https://i.scdn.co/image/alternate", width = 1200 } } }
            ]
        };
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler));

        var artwork = await client.GetPlaylistArtworkUriAsync("token",
            new ProviderArtworkReference(TracksRequest().PlaylistId), default);

        Assert.Equal("https://i.scdn.co/image/large", artwork.RequireValue().AbsoluteUri);
        Assert.Equal(0, handler.ThumbnailRequestCount);
    }

    [Fact]
    public async Task Cover_changes_have_distinct_references_when_the_playlist_revision_is_unchanged()
    {
        var handler = new ArtworkHandler();
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), new TestMemoryApplicationCache());
        var first = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;
        handler.CoverUrl = "https://i.scdn.co/image/second-cover";

        var second = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;

        Assert.Equal(first.SourceRevision, second.SourceRevision);
        Assert.NotEqual(first.Artwork!.Revision, second.Artwork!.Revision);
        Assert.Equal("https://i.scdn.co/image/first-cover",
            (await client.GetPlaylistArtworkUriAsync("token", first.Artwork, default)).RequireValue().AbsoluteUri);
        Assert.Equal(handler.CoverUrl,
            (await client.GetPlaylistArtworkUriAsync("token", second.Artwork, default)).RequireValue().AbsoluteUri);
        Assert.Equal(handler.CoverUrl,
            (await client.GetPlaylistArtworkUriAsync("token",
                new ProviderArtworkReference(second.Id, revision: second.SourceRevision), default)).RequireValue().AbsoluteUri);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Discovery_and_playlist_reads_use_the_same_cover_reference()
    {
        var handler = new ArtworkHandler();
        var client = new SpotifyPathfinderPlaylistClient(new HttpClient(handler), new TestMemoryApplicationCache());

        var discovery = (await client.GetUserPlaylistsAsync("token", new ProviderPageRequest(), null, default)).RequireValue();
        var playlist = (await client.GetPlaylistTracksAsync("token", TracksRequest(), default)).RequireValue().Playlist;

        Assert.Equal(Assert.Single(discovery.Items).Artwork, playlist.Artwork);
        Assert.Equal(handler.CoverUrl,
            (await client.GetPlaylistArtworkUriAsync("token", playlist.Artwork!, default)).RequireValue().AbsoluteUri);
        Assert.Equal(2, handler.RequestCount);
    }

    private static ProviderPlaylistTracksRequest TracksRequest() =>
        new(new("spotify", ProviderResourceKind.Playlist, "personal-playlist"), new ProviderPageRequest());

    private sealed class ArtworkHandler : HttpMessageHandler
    {
        public string CoverUrl { get; set; } = "https://i.scdn.co/image/first-cover";
        public string Name { get; set; } = "Release Radar";
        public string OwnerUsername { get; set; } = "listener";
        public object[]? ImageItems { get; set; }
        public int RequestCount { get; private set; }
        public int ThumbnailRequestCount { get; private set; }
        public string ThumbnailResponse { get; set; } = "{}";
        public HttpStatusCode ThumbnailStatus { get; set; } = HttpStatusCode.OK;
        public string? ThumbnailAuthorization { get; private set; }
        public Uri? ThumbnailRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri?.AbsolutePath == "/oembed")
            {
                ThumbnailRequestCount++;
                ThumbnailAuthorization = request.Headers.Authorization?.ToString();
                ThumbnailRequestUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(ThumbnailStatus)
                {
                    Content = new StringContent(ThumbnailResponse, Encoding.UTF8, "application/json")
                });
            }
            var playlist = new
            {
                uri = "spotify:playlist:personal-playlist",
                name = Name,
                ownerV2 = new { data = new { username = OwnerUsername } },
                revisionId = "unchanged-content",
                images = new { items = ImageItems ?? [new { sources = new[] { new { url = CoverUrl, width = 640, height = 640 } } }] },
                content = new { totalCount = 0, items = System.Array.Empty<object>() }
            };
            var payload = JsonSerializer.Serialize(new
            {
                data = new
                {
                    playlistV2 = playlist,
                    me = new { libraryV3 = new { totalCount = 1, items = new[] { new { item = new { data = playlist } } } } }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
