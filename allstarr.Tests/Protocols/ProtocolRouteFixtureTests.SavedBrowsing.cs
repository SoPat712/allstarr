using System.Net;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items?IncludeItemTypes=Audio&Recursive=true&Fields=DateCreated,Genres,MediaSources", "Audio", "ext-deezer-song-saved")]
    [InlineData("/Items?IncludeItemTypes=MusicAlbum&Recursive=true&SortBy=DateCreated&SortOrder=Descending&Fields=DateCreated", "MusicAlbum", "ext-deezer-album-saved")]
    [InlineData("/Artists?Recursive=true&Fields=Genres,DateCreated", "MusicArtist", "ext-deezer-artist-saved")]
    [InlineData("/Artists/AlbumArtists?Recursive=true&Fields=DateCreated", "MusicArtist", "ext-deezer-artist-saved")]
    public async Task FeishinFinampAndWeb_LibraryBrowseIsTheMediaServersOwnListWithoutSavedServiceItems(string path, string type, string id)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string native = """{"Id":"native","Type":"Audio","Name":"Native", "Future" : [1,true]}""";
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, $$"""{"Items":[{{native}}],"TotalRecordCount":1,"StartIndex":0}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + "&ApiKey=fixture&Limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(native),
            System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(items).GetRawText())));
        Assert.DoesNotContain(items, row => row.GetProperty("Id").GetString() == id && row.GetProperty("Type").GetString() == type);
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("&Filters=IsFavorite")]
    public async Task ParentBrowseWithoutSavedRowsSkipsLibraryAccessLookup(string favorite)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            await db.Set<FavoriteStateRecord>().ExecuteDeleteAsync();
            await db.Set<FavoriteEventRecord>().ExecuteDeleteAsync();
        }
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """{"Items":[{"Id":"native","Name":"Native","Type":"Audio"}],"TotalRecordCount":1,"StartIndex":0}"""),
            services =>
            {
                ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object);
                services.RemoveAll<IBackendLibraryAccessResolver>();
                services.AddSingleton(access.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?ApiKey=fixture&ParentId=native-album&IncludeItemTypes=Audio{favorite}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        access.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/Items/Latest")]
    [InlineData("/Users/user-1/Items/Latest")]
    public async Task RecentlyAddedListsOnlyWhatJoinedTheLibrary(string endpoint)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """[{"Id":"native","Name":"Native","Type":"Audio","DateCreated":"2000-01-01T00:00:00Z"}]"""),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&IncludeItemTypes=Audio&Limit=1&Fields=DateCreated,ParentId");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("native", Assert.Single(body.RootElement.EnumerateArray()).GetProperty("Id").GetString());
    }

    private static async Task SetSavedSongMetadataAsync(FavoriteViewFactory storage)
    {
        var display = new FavoriteDisplaySnapshot(1, "ext-deezer-song-saved", "song", "Saved song", ["Saved artist"], "Saved album", 180000, 2001)
        {
            ArtistIds = ["ext-deezer-artist-parent"],
            AlbumArtistIds = ["ext-deezer-artist-parent"],
            AlbumId = "ext-deezer-album-parent",
            Genres = ["Rock"]
        };
        await using var db = storage.CreateDbContext();
        await db.Set<FavoriteStateRecord>().Where(row => row.ItemId == display.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DisplayMetadataJson, JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }
}
