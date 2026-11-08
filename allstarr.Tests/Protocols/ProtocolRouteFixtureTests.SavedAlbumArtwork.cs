using System.Net;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedAlbumWithoutSourceArt_ShowsASavedSongCover(bool songHasCover)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            var display = new FavoriteDisplaySnapshot(1, "ext-deezer-song-saved", "song", "Iris", ["The Goo Goo Dolls"],
                "Dizzy up the Girl", 289533)
            { AlbumId = "ext-deezer-album-dizzy" };
            var json = JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == display.ItemId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson, json));
        }
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xD9];
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "dizzy"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-dizzy", Title = "Dizzy up the Girl" });
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "saved"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-saved", CoverArtUrl = songHasCover ? "https://images.example.test/song.jpg" : null });
        var media = new RecordingMediaAssetResolver();
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/song.jpg" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } },
            _ => throw new InvalidOperationException("Unexpected artwork request")
        }, services =>
        {
            ConfigureFavoriteViews(services, storage, gateway.Object);
            services.RemoveAll<IMediaAssetResolver>();
            services.AddSingleton<IMediaAssetResolver>(media);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items/ext-deezer-album-dizzy/Images/Primary?api_key=fixture");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        if (songHasCover) Assert.Equal(bytes, body);
        else Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, media.Identities.Count);
        Assert.Null(media.Identities[0].UserId);
        Assert.Equal(FavoriteViewer, media.Identities[1].UserId);
        Assert.NotEqual(media.Identities[0].Revision, media.Identities[1].Revision);
    }

    [Fact]
    public async Task SavedAlbumWithoutSourceDetails_OpensToItsSavedSongs()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            var display = new FavoriteDisplaySnapshot(1, "ext-deezer-song-saved", "song", "Iris", ["The Goo Goo Dolls"],
                "Dizzy up the Girl", 289533)
            { AlbumId = "ext-deezer-album-dizzy" };
            var json = JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == display.ItemId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson, json));
        }
        var gateway = new Mock<IProtocolProviderGateway>();
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""") : Json(200, """{"Items":[],"TotalRecordCount":0}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();

        using var item = JsonDocument.Parse(await client.GetStringAsync("/Users/user-1/Items/ext-deezer-album-dizzy?api_key=fixture"));
        Assert.StartsWith("Dizzy up the Girl", item.RootElement.GetProperty("Name").GetString());
        foreach (var query in new[] { "ParentId=ext-deezer-album-dizzy", "AlbumIds=ext-deezer-album-dizzy&Recursive=true" })
        {
            using var response = await client.GetAsync($"/Users/user-1/Items?{query}&IncludeItemTypes=Audio&api_key=fixture");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var songs = JsonDocument.Parse(body);
            Assert.Equal("ext-deezer-song-saved", Assert.Single(songs.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        }
    }

    private sealed class RecordingMediaAssetResolver : IMediaAssetResolver
    {
        public List<MediaAssetIdentity> Identities { get; } = [];

        public async Task<ResolvedMediaAsset?> ResolveAsync(MediaAssetIdentity identity,
            Func<CancellationToken, Task<MediaAssetSource?>> fetch, int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            Identities.Add(identity);
            var source = await fetch(cancellationToken);
            return source == null ? null : new(source.Bytes, source.ContentType,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source.Bytes)),
                source.ETag, source.LastModified, false);
        }
    }
}
