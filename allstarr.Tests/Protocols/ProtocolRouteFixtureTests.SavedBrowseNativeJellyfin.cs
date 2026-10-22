using System.Globalization;
using System.Net;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Artists", "", "MusicArtist", 558)]
    [InlineData("/Artists/AlbumArtists", "", "MusicArtist", 558)]
    [InlineData("/Artists", "&IncludeItemTypes=Audio", "MusicArtist", 558)]
    [InlineData("/Artists/AlbumArtists", "&IncludeItemTypes=MusicAlbum", "MusicArtist", 558)]
    [InlineData("/Items", "&IncludeItemTypes=MusicArtist", "MusicArtist", 558)]
    [InlineData("/Items", "&IncludeItemTypes=MusicArtist&EnableTotalRecordCount=false", "MusicArtist", 558)]
    [InlineData("/Items", "&IncludeItemTypes=MusicAlbum", "MusicAlbum", 899)]
    [InlineData("/Items", "&IncludeItemTypes=Audio", "Audio", 2226)]
    [InlineData("/Users/user-1/Items", "&IncludeItemTypes=MusicArtist", "MusicArtist", 558)]
    [InlineData("/Users/user-1/Items", "&IncludeItemTypes=MusicAlbum", "MusicAlbum", 899)]
    [InlineData("/Users/user-1/Items", "&IncludeItemTypes=Audio", "Audio", 2226)]
    public async Task JellyfinSavedBrowsePreservesLargeNativeCollectionsAndPagesTheUnion(
        string endpoint, string selection, string type, int nativeCount)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSingleSavedSongAsync(storage, albumIdentity: true);
        var native = NativeBrowseRows(type, nativeCount);
        var queries = new List<IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues>>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            queries.Add(query);
            var accepted = !endpoint.StartsWith("/Artists", StringComparison.Ordinal) ||
                query.GetValueOrDefault("IncludeItemTypes").ToString() == QueryHelpers.ParseQuery(selection).GetValueOrDefault("IncludeItemTypes").ToString();
            var start = int.TryParse(query.GetValueOrDefault("StartIndex"), out var offset) ? offset : 0;
            var limit = int.TryParse(query.GetValueOrDefault("Limit"), out var size) ? size : 100;
            IEnumerable<string> rows = accepted ? native : [];
            if (query.GetValueOrDefault("SortOrder") == "Descending") rows = rows.Reverse();
            return Json(200, $$"""{"Items":[{{string.Join(',', rows.Skip(start).Take(limit))}}],"TotalRecordCount":{{(query.GetValueOrDefault("EnableTotalRecordCount") == "false" ? 0 : accepted ? nativeCount : 0)}},"StartIndex":{{start}},"FutureEnvelope":7}""");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        var expectedSaved = type switch
        {
            "MusicArtist" => "ext-apple-download-artist-twice",
            "MusicAlbum" => "ext-apple-download-album-saved",
            _ => "ext-apple-download-song-saved"
        };
        foreach (var descending in new[] { false, true })
        {
            var order = descending ? "Descending" : "Ascending";
            using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&userId=user-1&Limit=3000&SortBy=SortName&SortOrder={order}{selection}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(nativeCount + 1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(7, body.RootElement.GetProperty("FutureEnvelope").GetInt32());
            var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(nativeCount + 1, items.Length);
            Assert.Single(items, row => row.GetProperty("Id").GetString() == expectedSaved);
            var originals = items.Where(row => row.GetProperty("Id").GetString()!.StartsWith("native-", StringComparison.Ordinal))
                .ToDictionary(row => row.GetProperty("Id").GetString()!, row => row.GetRawText());
            for (var index = 0; index < nativeCount; index++) Assert.Equal(native[index], originals[$"native-{index:D4}"]);
            var names = items.Select(row => row.GetProperty("Name").GetString()!).ToArray();
            Assert.Equal(descending ? names.OrderDescending(StringComparer.OrdinalIgnoreCase) : names.Order(StringComparer.OrdinalIgnoreCase), names);
            var expectedIds = items.Select(row => row.GetProperty("Id").GetString()).ToArray();
            var pageIds = new List<string?>();
            for (var start = 0; start < nativeCount + 1; start += 499)
            {
                using var pageResponse = await client.GetAsync($"{endpoint}?ApiKey=fixture&userId=user-1&StartIndex={start}&Limit=499&SortBy=SortName&SortOrder={order}{selection}");
                Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
                using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
                Assert.Equal(nativeCount + 1, page.RootElement.GetProperty("TotalRecordCount").GetInt32());
                Assert.Equal(start, page.RootElement.GetProperty("StartIndex").GetInt32());
                pageIds.AddRange(page.RootElement.GetProperty("Items").EnumerateArray().Select(row => row.GetProperty("Id").GetString()));
            }
            Assert.Equal(expectedIds, pageIds);
        }
        Assert.Contains(queries, query => query.GetValueOrDefault("StartIndex") == "500");
    }

    [Theory]
    [InlineData("/Artists/AlbumArtists", "", "MusicArtist", 558, false)]
    [InlineData("/Users/user-1/Items", "&IncludeItemTypes=Audio", "Audio", 2226, false)]
    [InlineData("/Items", "&IncludeItemTypes=MusicAlbum", "MusicAlbum", 899, false)]
    [InlineData("/Items", "&IncludeItemTypes=MusicAlbum", "MusicAlbum", 899, true)]
    public async Task JellyfinSingleSavedSongAddsOnlyIdentifiableParentsAndUnheartRestoresNativeCounts(
        string endpoint, string selection, string type, int nativeCount, bool albumIdentity)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSingleSavedSongAsync(storage, albumIdentity);
        var native = NativeBrowseRows(type, nativeCount);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var count = endpoint.StartsWith("/Artists", StringComparison.Ordinal) && query.ContainsKey("IncludeItemTypes") ? 0 : nativeCount;
            var start = int.TryParse(query.GetValueOrDefault("StartIndex"), out var offset) ? offset : 0;
            var limit = int.TryParse(query.GetValueOrDefault("Limit"), out var size) ? size : 100;
            return Json(200, $$"""{"Items":[{{string.Join(',', native.Take(count).Skip(start).Take(limit))}}],"TotalRecordCount":{{count}},"StartIndex":{{start}}}""");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        foreach (var hearted in new[] { false, true, false })
        {
            await using (var db = storage.CreateDbContext())
                await db.Set<FavoriteStateRecord>().Where(row => row.ItemId == "ext-apple-download-song-saved")
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsFavorite, hearted));
            using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&userId=user-1&Limit=2000{selection}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var total = nativeCount + (hearted && (type != "MusicAlbum" || albumIdentity) ? 1 : 0);
            Assert.Equal(total, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(Math.Min(2000, total), items.Length);
            var names = items.Select(row => row.GetProperty("Name").GetString()).ToArray();
            Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
            if (type == "Audio" && hearted) Assert.Contains(items, row => row.GetProperty("Id").GetString() == "ext-apple-download-song-saved");
            if (type == "MusicArtist")
            {
                Assert.Equal(nativeCount, items.Count(row => row.GetProperty("Id").GetString()!.StartsWith("native-", StringComparison.Ordinal)));
                Assert.Equal(hearted ? 1 : 0, items.Count(row => row.GetProperty("Id").GetString() == "ext-apple-download-artist-twice"));
            }
        }
    }

    [Theory]
    [InlineData("/Genres", "Audio")]
    [InlineData("/Genres", "MusicAlbum")]
    [InlineData("/Genres", "MusicArtist")]
    [InlineData("/Items/Latest", "Audio")]
    [InlineData("/Items/Latest", "MusicAlbum")]
    [InlineData("/Users/user-1/Items/Latest", "Audio")]
    [InlineData("/Users/user-1/Items/Latest", "MusicAlbum")]
    [InlineData("/Items", "Audio")]
    public async Task JellyfinSavedFacetsLatestAndRandomKeepEveryNativeRow(string endpoint, string type)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSingleSavedSongAsync(storage, albumIdentity: true);
        var genres = endpoint == "/Genres";
        var latest = endpoint.EndsWith("Latest", StringComparison.Ordinal);
        var native = NativeBrowseRows(genres ? "MusicGenre" : type, 558);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var start = int.TryParse(query.GetValueOrDefault("StartIndex"), out var offset) ? offset : 0;
            var limit = int.TryParse(query.GetValueOrDefault("Limit"), out var size) ? size : 100;
            if (latest) start = 0;
            var source = query.GetValueOrDefault("SortBy") == "Random" && start != 0 ? native.Reverse() : native;
            var rows = string.Join(',', source.Skip(start).Take(limit));
            return Json(200, latest ? $"[{rows}]" : $$"""{"Items":[{{rows}}],"TotalRecordCount":558,"StartIndex":{{start}}}""");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        var sorting = latest ? "" : genres ? "&SortBy=SortName&SortOrder=Descending" : "&SortBy=Random";
        using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&IncludeItemTypes={type}&Fields=DateCreated&Limit=2000{sorting}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = latest ? body.RootElement : body.RootElement.GetProperty("Items");
        Assert.Equal(559, rows.GetArrayLength());
        if (!latest) Assert.Equal(559, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var originals = rows.EnumerateArray().Where(row => row.GetProperty("Id").GetString()!.StartsWith("native-", StringComparison.Ordinal))
            .ToDictionary(row => row.GetProperty("Id").GetString()!, row => row.GetRawText());
        for (var index = 0; index < native.Length; index++) Assert.Equal(native[index], originals[$"native-{index:D4}"]);
        if (genres)
        {
            var names = rows.EnumerateArray().Select(row => row.GetProperty("Name").GetString()).ToArray();
            Assert.Equal(names.OrderDescending(StringComparer.OrdinalIgnoreCase), names);
        }
        if (latest) Assert.Equal(type == "Audio" ? "ext-apple-download-song-saved" : "ext-apple-download-album-saved", rows[0].GetProperty("Id").GetString());
        if (latest || genres)
        {
            var expected = rows.EnumerateArray().Select(row => row.GetProperty("Id").GetString()).ToArray();
            var received = new List<string?>();
            for (var start = 0; start < expected.Length; start += 499)
            {
                using var pageResponse = await client.GetAsync($"{endpoint}?ApiKey=fixture&IncludeItemTypes={type}&Fields=DateCreated&StartIndex={start}&Limit=499{sorting}");
                Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
                using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
                var pageRows = latest ? page.RootElement : page.RootElement.GetProperty("Items");
                if (!latest) Assert.Equal(559, page.RootElement.GetProperty("TotalRecordCount").GetInt32());
                foreach (var row in pageRows.EnumerateArray().Where(row => row.GetProperty("Id").GetString()!.StartsWith("native-", StringComparison.Ordinal)))
                    Assert.Equal(originals[row.GetProperty("Id").GetString()!], row.GetRawText());
                received.AddRange(pageRows.EnumerateArray().Select(row => row.GetProperty("Id").GetString()));
            }
            Assert.Equal(expected, received);
        }
    }

    [Theory]
    [InlineData("/Items/Filters")]
    [InlineData("/Items/Filters2")]
    public async Task JellyfinSavedBrowseRetainsNativeGenreAndYearFacets(string endpoint)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSingleSavedSongAsync(storage, albumIdentity: true);
        var detailed = endpoint.EndsWith('2');
        var genres = Enumerable.Range(0, 558).Select(index => detailed
            ? $$"""{"Id":"native-{{index:D4}}", "Name":"Genre {{index:D4}}", "Future" : [1,true]}"""
            : JsonSerializer.Serialize($"Genre {index:D4}")).ToArray();
        var years = Enumerable.Range(1000, 558).ToArray();
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, $$"""{"Genres":[{{string.Join(',', genres)}}],"Years":{{JsonSerializer.Serialize(years)}},"Tags":["Keep"]}"""),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"{endpoint}?ApiKey=fixture&IncludeItemTypes=Audio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(559, body.RootElement.GetProperty("Genres").GetArrayLength());
        foreach (var genre in genres) Assert.Contains(body.RootElement.GetProperty("Genres").EnumerateArray(), row => row.GetRawText() == genre);
        Assert.Equal(years.Append(2001), body.RootElement.GetProperty("Years").EnumerateArray().Select(row => row.GetInt32()));
        Assert.Equal("Keep", body.RootElement.GetProperty("Tags")[0].GetString());
    }

    [Theory]
    [InlineData("MusicArtist", "ext-apple-download-artist-twice")]
    [InlineData("MusicAlbum", "ext-apple-download-album-saved")]
    [InlineData("Audio", "ext-apple-download-song-saved")]
    public async Task JellyfinSavedBrowseDeduplicatesIdsWithoutReplacingNativeRows(string type, string savedId)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSingleSavedSongAsync(storage, albumIdentity: true);
        var duplicate = $$"""{"Id":"{{savedId}}", "Name":"Existing saved row", "Type":"{{type}}", "Future" : [7,true]}""";
        var native = NativeBrowseRows(type, 558).Append(duplicate).ToArray();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var start = int.TryParse(query.GetValueOrDefault("StartIndex"), out var offset) ? offset : 0;
            var limit = int.TryParse(query.GetValueOrDefault("Limit"), out var size) ? size : 100;
            return Json(200, $$"""{"Items":[{{string.Join(',', native.Skip(start).Take(limit))}}],"TotalRecordCount":559,"StartIndex":{{start}}}""");
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IncludeItemTypes={type}&Limit=2000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(559, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(559, rows.Length);
        Assert.Equal(duplicate, Assert.Single(rows, row => row.GetProperty("Id").GetString() == savedId).GetRawText());
        var originals = rows.ToDictionary(row => row.GetProperty("Id").GetString()!, row => row.GetRawText());
        for (var index = 0; index < 558; index++) Assert.Equal(native[index], originals[$"native-{index:D4}"]);
    }

    private static string[] NativeBrowseRows(string type, int count) => Enumerable.Range(0, count).Select(index =>
        $$"""{"Id":"native-{{index:D4}}", "Name":"{{(index < count / 2 ? "A" : "Z")}} {{index:D4}}", "Type":"{{type}}", "DateCreated":"2000-01-01T00:00:00Z", "Future" : [{{index}},true]}""").ToArray();

    private static async Task SeedSingleSavedSongAsync(FavoriteViewFactory storage, bool albumIdentity)
    {
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var display = new FavoriteDisplaySnapshot(1, "ext-apple-download-song-saved", "song", "TWICE", ["TWICE"], "Saved album", 180000, 2001)
        {
            ArtistIds = ["ext-apple-download-artist-twice"],
            AlbumArtistIds = ["ext-apple-download-artist-twice"],
            AlbumArtists = ["TWICE"],
            AlbumId = albumIdentity ? "ext-apple-download-album-saved" : null,
            Genres = ["Rock"]
        };
        await using var db = storage.CreateDbContext();
        await db.Set<FavoriteStateRecord>().Where(row => row.ItemId != "ext-deezer-song-saved")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsFavorite, false));
        await db.Set<FavoriteStateRecord>().Where(row => row.ItemId == "ext-deezer-song-saved")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ItemId, display.ItemId)
                .SetProperty(row => row.DisplayMetadataJson, JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await db.Set<FavoriteEventRecord>().Where(row => row.ItemId == "ext-deezer-song-saved")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ItemId, display.ItemId)
                .SetProperty(row => row.CreatedAt, DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture)));
    }
}
