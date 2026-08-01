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
    public async Task SearchUsesGatewayLimitAndStableProviderId()
    {
        var handler = new Handler();
        var service = new AppleMusicMetadataService(
            new Factory(new HttpClient(handler)),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-gateway:8000/" }),
            NullLogger<AppleMusicMetadataService>.Instance);

        var songs = await service.SearchSongsAsync("Choosin' Texas", 200);

        var song = Assert.Single(songs);
        Assert.Equal("apple-download", song.ExternalProvider);
        Assert.Equal("ext-apple-download-song-101", song.Id);
        Assert.Equal("ext-apple-download-artist-201", song.ArtistId);
        Assert.Equal(["ext-apple-download-artist-201"], song.ArtistIds);
        Assert.Equal("ext-apple-download-album-301", song.AlbumId);
        Assert.Contains("limit=100", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task ArtistAndAlbumRelationshipsAreOpenable()
    {
        var handler = new Handler();
        var service = new AppleMusicMetadataService(
            new Factory(new HttpClient(handler)),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-gateway:8000/" }),
            NullLogger<AppleMusicMetadataService>.Instance);

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

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/artist/201" =>
                    """{"id":"201","name":"Ella Langley","image_url":"https://example.test/art.jpg"}""",
                "/api/artist/201/albums" =>
                    """[{"id":"301","title":"Dandelion","artist":"Ella Langley","artist_id":"201","cover_url":"https://example.test/art.jpg","release_date":"2026-01-01","track_count":1}]""",
                "/api/album/301" =>
                    """{"id":"301","title":"Dandelion","artist":"Ella Langley","artist_id":"201","cover_url":"https://example.test/art.jpg","release_date":"2026-01-01","track_count":1,"tracks":[{"id":"101","title":"Choosin' Texas","artist":"Ella Langley","artist_id":"201","album":"Dandelion","album_id":"301","duration":231,"cover_url":"https://example.test/art.jpg"}]}""",
                _ =>
                    """[{"id":"101","title":"Choosin' Texas","artist":"Ella Langley","artist_id":"201","album":"Dandelion","album_id":"301","duration":231,"cover_url":"https://example.test/art.jpg"}]"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
