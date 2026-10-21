using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task SongAliasRoutes_ReturnTheSameDisplayMetadataAndNavigableAlbum(string backend)
    {
        const string publicId = "ext-allstarr-song-01a1540a-0842-7777-969e-50303caad5c3";
        const string albumId = "ext-apple-download-album-1780787475";
        string[] aliases = ["ext-spotiflac-soundcloud-song-1886663583", "ext-deezer-song-2925172991",
            "ext-spotiflac-tidal-web-song-tidal:378292110", "ext-apple-musickit-song-1780787517"];
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Song[]
            {
                new() { Id = publicId, Title = "Check", Artist = "FLO", Album = "Check",
                    ExternalProvider = "spotiflac-soundcloud", ExternalId = "1886663583" },
                new() { Id = publicId, Title = "Check", Artist = "FLO", Album = "Access All Areas",
                    AlbumArtist = "FLO", AlbumId = albumId, ExternalProvider = "apple-musickit",
                    ExternalId = "1780787517", Year = 2024, Isrc = "GBUM72403956", CoverArtUrl = "https://images.example.test/album" }
            });
        var local = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict), new ProviderRegistry([]),
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), identities: identities.Object);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((ProtocolExecutionContext context, string provider, string id) => local.GetSongAsync(context, provider, id));
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "1780787475"))
            .ReturnsAsync(new Album
            {
                Id = albumId,
                Title = "Access All Areas",
                Artist = "FLO",
                ExternalProvider = "apple-download",
                ExternalId = "1780787475",
                Songs = [new Song { Id = publicId, Title = "Check", Artist = "FLO",
                    Album = "Access All Areas", AlbumId = albumId, ExternalProvider = "apple-download", ExternalId = "1780787517" }]
            });
        using var host = new ProtocolFactory(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath.Contains("ping", StringComparison.Ordinal))
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri.AbsolutePath);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = host.CreateClient();
        JsonElement? first = null;
        foreach (var alias in aliases)
        {
            using var response = await client.GetAsync(backend == "Jellyfin"
                ? $"/Users/user-1/Items/{alias}?api_key=fixture-key"
                : $"/rest/getSong?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id={alias}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(content);
            if (backend == "Subsonic") Assert.True(body.RootElement.GetProperty("subsonic-response").TryGetProperty("song", out _), content);
            var song = backend == "Jellyfin" ? body.RootElement : body.RootElement.GetProperty("subsonic-response").GetProperty("song");
            Assert.Equal(publicId, song.GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
            Assert.Equal(albumId, song.GetProperty(backend == "Jellyfin" ? "AlbumId" : "albumId").GetString());
            if (first.HasValue)
                foreach (var field in backend == "Jellyfin"
                    ? new[] { "Id", "Name", "Artists", "Album", "AlbumArtist", "AlbumId", "ParentId", "ProductionYear", "ProviderIds", "ImageTags" }
                    : new[] { "id", "title", "artist", "album", "albumId", "year", "coverArt" })
                    Assert.Equal(first.Value.GetProperty(field).GetRawText(), song.GetProperty(field).GetRawText());
            if (backend == "Jellyfin")
            {
                using var idsResponse = await client.GetAsync($"/Items?Ids={alias}&Fields=ParentId,ProviderIds&api_key=fixture-key");
                Assert.Equal(HttpStatusCode.OK, idsResponse.StatusCode);
                using var idsBody = JsonDocument.Parse(await idsResponse.Content.ReadAsStringAsync());
                var selected = Assert.Single(idsBody.RootElement.GetProperty("Items").EnumerateArray());
                foreach (var field in new[] { "Id", "Name", "Artists", "Album", "AlbumId", "ParentId", "ProductionYear", "ProviderIds", "ImageTags" })
                    Assert.Equal(song.GetProperty(field).GetRawText(), selected.GetProperty(field).GetRawText());
            }
            first = song.Clone();
        }
        using var albumResponse = await client.GetAsync(backend == "Jellyfin"
            ? $"/Items/{albumId}?api_key=fixture-key"
            : $"/rest/getAlbum?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id={albumId}");
        Assert.Equal(HttpStatusCode.OK, albumResponse.StatusCode);
        using var albumBody = JsonDocument.Parse(await albumResponse.Content.ReadAsStringAsync());
        var album = backend == "Jellyfin" ? albumBody.RootElement : albumBody.RootElement.GetProperty("subsonic-response").GetProperty("album");
        Assert.Equal(albumId, album.GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
        if (backend == "Jellyfin")
        {
            using var tracksResponse = await client.GetAsync($"/Items?ParentId={albumId}&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, tracksResponse.StatusCode);
            using var tracks = JsonDocument.Parse(await tracksResponse.Content.ReadAsStringAsync());
            Assert.Equal(publicId, Assert.Single(tracks.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        }
        else Assert.Equal(publicId, Assert.Single(album.GetProperty("song").EnumerateArray()).GetProperty("id").GetString());
    }
}
