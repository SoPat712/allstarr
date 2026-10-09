using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class AppleMusicMetadataServiceTests
{
    [Fact]
    public async Task SearchUsesCatalogLimitAndStablePublicProviderId()
    {
        var handler = new Handler();
        var service = Create(new HttpClient(handler));

        var songs = await service.SearchSongsAsync("Choosin' Texas", 200);

        var song = Assert.Single(songs);
        Assert.Equal("apple-download", song.ExternalProvider);
        Assert.Equal("ext-apple-download-song-101", song.Id);
        Assert.Equal("ext-apple-download-artist-201", song.ArtistId);
        Assert.Equal(["ext-apple-download-artist-201"], song.ArtistIds);
        Assert.Equal("ext-apple-download-album-301", song.AlbumId);
        Assert.Contains("limit=25", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task ArtistAndAlbumRelationshipsAreOpenable()
    {
        var handler = new Handler();
        var service = Create(new HttpClient(handler));

        var artist = await service.GetArtistAsync("apple-download", "201");
        var albums = await service.GetArtistAlbumsAsync("apple-download", "201");
        var tracks = await service.GetArtistTracksAsync("apple-download", "201");
        var album = await service.GetAlbumAsync("apple-download", "301");

        Assert.Equal("ext-apple-download-artist-201", artist!.Id);
        Assert.Equal("ext-apple-download-album-301", Assert.Single(albums).Id);
        Assert.Equal("ext-apple-download-artist-201", albums[0].ArtistId);
        Assert.Equal("ext-apple-download-song-101", Assert.Single(tracks).Id);
        Assert.Equal("ext-apple-download-song-101", Assert.Single(album!.Songs).Id);
        Assert.Equal("ext-apple-download-artist-201", album.Songs[0].ArtistId);
    }

    [Fact]
    public async Task SearchMapsOpenableAlbumsAndArtists()
    {
        var service = Create(new HttpClient(new Handler()));

        var album = Assert.Single(await service.SearchAlbumsAsync("Dandelion"));
        var artist = Assert.Single(await service.SearchArtistsAsync("Ella Langley"));

        Assert.Equal("ext-apple-download-album-301", album.Id);
        Assert.Equal("ext-apple-download-artist-201", album.ArtistId);
        Assert.Equal("ext-apple-download-artist-201", artist.Id);
        Assert.Equal("apple-download", artist.ExternalProvider);
    }

    private static AppleMusicMetadataService Create(HttpClient http)
    {
        var client = AppleProviderTestFactory.Client(http);
        return new(new(client), new(client, http));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Assert.Equal("amp-api.music.apple.com", RequestUri!.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            var song = new { id = "101", attributes = new { name = "Choosin' Texas", artistName = "Ella Langley", albumName = "Dandelion", durationInMillis = 231000 }, relationships = new { artists = new { data = new[] { new { id = "201" } } }, albums = new { data = new[] { new { id = "301" } } } } };
            var album = new { id = "301", attributes = new { name = "Dandelion", artistName = "Ella Langley", trackCount = 1 }, relationships = new { artists = new { data = new[] { new { id = "201" } } } } };
            var artist = new { id = "201", attributes = new { name = "Ella Langley" } };
            var path = RequestUri.AbsolutePath;
            object body = path switch
            {
                "/v1/catalog/us/search" when RequestUri.Query.Contains("types=albums") => new { results = new { albums = new { data = new[] { album } } } },
                "/v1/catalog/us/search" when RequestUri.Query.Contains("types=artists") => new { results = new { artists = new { data = new[] { artist } } } },
                "/v1/catalog/us/search" => new { results = new { songs = new { data = new[] { song } } } },
                "/v1/catalog/us/artists/201" => new { data = new[] { artist } },
                "/v1/catalog/us/artists/201/albums" or "/v1/catalog/us/albums/301" => new { data = new[] { album } },
                _ => new { data = new[] { song } }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
}
