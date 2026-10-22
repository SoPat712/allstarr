using System.Net;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items?IncludeItemTypes=Audio&Recursive=true&Fields=DateCreated,Genres,MediaSources", "Audio", "ext-deezer-song-saved")]
    [InlineData("/Items?IncludeItemTypes=MusicAlbum&Recursive=true&SortBy=DateCreated&SortOrder=Descending&Fields=DateCreated", "MusicAlbum", "ext-deezer-album-saved")]
    [InlineData("/Artists?Recursive=true&Fields=Genres,DateCreated", "MusicArtist", "ext-deezer-artist-saved")]
    [InlineData("/Artists/AlbumArtists?Recursive=true&Fields=DateCreated", "MusicArtist", "ext-deezer-artist-saved")]
    public async Task FeishinFinampAndWeb_NormalBrowseIncludesSavedItemsWithoutProviderRequests(string path, string type, string id)
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
        Assert.Equal(native, Assert.Single(items, row => row.GetProperty("Id").GetString() == "native").GetRawText());
        var saved = Assert.Single(items, row => row.GetProperty("Id").GetString() == id);
        Assert.Equal(type, saved.GetProperty("Type").GetString());
        Assert.True(saved.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
        Assert.True(saved.GetProperty("DateCreated").TryGetDateTimeOffset(out _));
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/Genres?IncludeItemTypes=Audio", "Items")]
    [InlineData("/Items/Filters2?IncludeItemTypes=Audio", "Genres")]
    [InlineData("/Items/Filters?IncludeItemTypes=Audio", "Years")]
    public async Task WebAndFinamp_BrowseFacetsIncludeOnlySavedMetadata(string path, string property)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, path.StartsWith("/Genres", StringComparison.Ordinal) ? """{"Items":[],"TotalRecordCount":0}""" : """{"Genres":[],"Years":[]}"""),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + "&ApiKey=fixture");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(body.RootElement.GetProperty(property).EnumerateArray());
    }

    [Theory]
    [InlineData("", 0, 2)]
    [InlineData("&ParentId=music-1", 0, 2)]
    [InlineData("", 1, 2)]
    [InlineData("", 2, 2)]
    [InlineData("&ParentId=foreign-folder", 0, 1)]
    public async Task JellyfinSavedBrowsingPagesAfterMergingAndHonorsFolderOwnership(string scope, int start, int total)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """{"Items":[{"Id":"native","Name":"Native","Type":"Audio","DateCreated":"2000-01-01T00:00:00Z"}],"TotalRecordCount":1,"StartIndex":0}"""),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object),
            configuration: new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = "music-1" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IncludeItemTypes=Audio&Recursive=true&SortBy=DateCreated&SortOrder=Descending&Fields=DateCreated,ParentId&StartIndex={start}&Limit=1{scope}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(total, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        if (total == 2)
        {
            Assert.Equal(start, body.RootElement.GetProperty("StartIndex").GetInt32());
            var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            if (start == 2) Assert.Empty(items);
            else Assert.Equal(start == 0 ? "ext-deezer-song-saved" : "native", Assert.Single(items).GetProperty("Id").GetString());
        }
    }

    [Theory]
    [InlineData("/Items/Latest")]
    [InlineData("/Users/user-1/Items/Latest")]
    public async Task WebAndFinampLatestUseMembershipDateAndReturnTheOpenApiArrayShape(string endpoint)
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
        var item = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("ext-deezer-song-saved", item.GetProperty("Id").GetString());
        var data = item.GetProperty("UserData");
        Assert.Equal(JsonValueKind.Number, data.GetProperty("PlaybackPositionTicks").ValueKind);
        Assert.Equal(JsonValueKind.Number, data.GetProperty("PlayCount").ValueKind);
        Assert.Equal(JsonValueKind.False, data.GetProperty("Played").ValueKind);
    }

    [Theory]
    [InlineData("Genres=Rock", 2)]
    [InlineData("Years=2001", 2)]
    [InlineData("MinYear=2002", 1)]
    [InlineData("NameStartsWith=Z", 1)]
    [InlineData("NameStartsWithOrGreater=Z", 1)]
    [InlineData("NameLessThan=A", 1)]
    [InlineData("SortBy=Random", 2)]
    public async Task FinampBrowseFiltersStoredMembershipBeforeCounting(string filter, int total)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """{"Items":[{"Id":"native","Name":"Native","Type":"Audio"}],"TotalRecordCount":1,"StartIndex":0}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Users/user-1/Items?ApiKey=fixture&IncludeItemTypes=Audio&Recursive=true&Limit=20&{filter}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(total, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinSavedAlbumLibraryViewIsPartialWhileDirectAlbumOpeningStaysComplete(bool libraryView)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        if (!libraryView)
            gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "parent"))
                .ReturnsAsync(new Album
                {
                    Id = "ext-deezer-album-parent",
                    Title = "Saved album",
                    Artist = "Saved artist",
                    Songs =
                    [
                        new Song { Id = "ext-deezer-song-saved", Title = "Saved", AlbumId = "ext-deezer-album-parent", ExternalProvider = "deezer", ExternalId = "saved" },
                        new Song { Id = "ext-deezer-song-unsaved", Title = "Unsaved", AlbumId = "ext-deezer-album-parent", ExternalProvider = "deezer", ExternalId = "unsaved" }
                    ]
                });
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""") : Json(200, """{"Items":[],"TotalRecordCount":0}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object),
            configuration: new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = "music-1" });
        using var client = factory.CreateClient();
        var scope = libraryView ? "&ParentId=music-1" : "";
        using var response = await client.GetAsync("/Items?ApiKey=fixture&IncludeItemTypes=Audio&AlbumIds=ext-deezer-album-parent&Limit=20" + scope);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(libraryView ? 1 : 2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(!libraryView, items.Any(item => item.GetProperty("Id").GetString() == "ext-deezer-song-unsaved"));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("native-rock")]
    [InlineData("genre-rock")]
    public async Task JellyfinSavedGenreIdsIncludeMatchingNativeAndProviderSongs(string genreId)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string native = """{"Id":"native-song", "Name":"Native", "Type":"Audio", "future" : [1,true]}""";
        var queries = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path == "/Users/user-1/Items/native-rock") return Json(200, """{"Id":"native-rock","Name":"Rock","Type":"MusicGenre"}""");
            queries.Add(request.RequestUri.Query);
            return Json(200, $$"""{"Items":[{{native}}],"TotalRecordCount":1,"StartIndex":0}""");
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IncludeItemTypes=Audio&Recursive=true&GenreIds={genreId}&Limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Contains(native, text, StringComparison.Ordinal);
        Assert.Contains(body.RootElement.GetProperty("Items").EnumerateArray(), item => item.GetProperty("Id").GetString() == "ext-deezer-song-saved");
        Assert.Contains(queries, query => genreId == "genre-rock"
            ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)["Genres"] == "Rock"
            : Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)["GenreIds"] == "native-rock");
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("&IncludeItemTypes=")]
    public async Task JellyfinSavedUntypedBrowseKeepsNativePlaylistAndGenreRows(string types)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        const string playlist = """{"Id":"native-playlist", "Type":"Playlist", "Name":"Native playlist", "future" : [1,true]}""";
        const string genre = """{"Id":"native-genre", "Type":"MusicGenre", "Name":"Native genre", "future" : [2,true]}""";
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var types = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["IncludeItemTypes"].ToString().Split(',');
            var rows = new List<string>();
            if (types.Contains("Playlist")) rows.Add(playlist);
            if (types.Contains("MusicGenre")) rows.Add(genre);
            return Json(200, $$"""{"Items":[{{string.Join(',', rows)}}],"TotalRecordCount":{{rows.Count}},"StartIndex":0}""");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?ApiKey=fixture&Recursive=true&Limit=20" + types);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(playlist, text, StringComparison.Ordinal);
        Assert.Contains(genre, text, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(5, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData("/Items", false)]
    [InlineData("/Items", true)]
    [InlineData("/Items/Latest", false)]
    [InlineData("/Items/Latest", true)]
    public async Task WebSavedNewestSortUsesNativeDatesWithoutChangingSelectedNativeRows(string endpoint, bool includeDate)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var at = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(500);
        await using (var db = storage.CreateDbContext())
            await db.Set<FavoriteEventRecord>().Where(row => row.ItemId == "ext-deezer-album-saved")
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.CreatedAt, at));
        var later = """{"Id":"native-later","Name":"Native later","Type":"MusicAlbum",DATE"future" : [1,true]}"""
            .Replace("DATE", includeDate ? "\"DateCreated\":\"2024-01-01T00:00:01Z\"," : "", StringComparison.Ordinal);
        var earlier = """{"Id":"native-earlier","Name":"Native earlier","Type":"MusicAlbum",DATE"future" : [2,true]}"""
            .Replace("DATE", includeDate ? "\"DateCreated\":\"2024-01-01T00:00:00Z\"," : "", StringComparison.Ordinal);
        var metadataReads = 0;
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path == "/System/Info/Public") return Json(200, """{"Id":"e3a56afe7c3846f18b60519c78aad531"}""");
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (path == "/Users/user-1/Items" && query.ContainsKey("Ids"))
            {
                metadataReads++;
                Assert.Equal("DateCreated", query["Fields"].ToString());
                return Json(200, """{"Items":[{"Id":"native-later","DateCreated":"2024-01-01T00:00:01Z"},{"Id":"native-earlier","DateCreated":"2024-01-01T00:00:00Z"}],"TotalRecordCount":2}""");
            }
            var rows = "[" + later + "," + earlier + "]";
            return Json(200, endpoint.EndsWith("Latest", StringComparison.Ordinal) ? rows
                : "{\"Items\":" + rows + ",\"TotalRecordCount\":2,\"StartIndex\":0}");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        var fields = includeDate ? "SortName,DateCreated" : "PrimaryImageAspectRatio,SortName";
        using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&IncludeItemTypes=MusicAlbum&SortBy=DateCreated&SortOrder=Descending&Fields={fields}&Limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(later, text, StringComparison.Ordinal);
        Assert.Contains(earlier, text, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(text);
        var items = endpoint.EndsWith("Latest", StringComparison.Ordinal) ? body.RootElement : body.RootElement.GetProperty("Items");
        Assert.Equal(["native-later", "ext-deezer-album-saved", "native-earlier"],
            items.EnumerateArray().Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal(!includeDate, metadataReads > 0);
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
