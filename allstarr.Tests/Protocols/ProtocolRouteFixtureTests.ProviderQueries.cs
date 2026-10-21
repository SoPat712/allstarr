using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Favorites;
using Microsoft.EntityFrameworkCore;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("AlbumIds=ext-deezer-album-one,ext-deezer-album-two,ext-deezer-album-one", 1, 1, 3, "c")]
    [InlineData("ParentId=ext-deezer-album-one", 0, 0, 2, "")]
    [InlineData("AlbumIds=ext-deezer-album-one,ext-deezer-album-two&SearchTerm=keep&Genres=Rock&Years=2020&ExcludeItemIds=ext-deezer-song-c", 0, 10, 1, "b")]
    [InlineData("AlbumIds=ext-deezer-album-one,ext-deezer-album-two&GenreIds=genre-rock&MinYear=2020&MaxYear=2020", 0, 10, 2, "b,c")]
    [InlineData("AlbumIds=ext-deezer-album-one&ExcludeArtistIds=ext-deezer-artist-one", 0, 10, 0, "")]
    [InlineData("AlbumIds=ext-deezer-album-one&ExcludeItemTypes=Audio", 0, 10, 0, "")]
    [InlineData("AlbumIds=ext-deezer-album-one&ExcludeLocationTypes=FileSystem", 0, 10, 0, "")]
    public async Task JellyfinFeishinAlbumQuery_FiltersSortsCountsThenPages(string scope, int start, int limit, int total, string expected)
    {
        var gateway = ProviderQueryGateway();
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?{scope}&IncludeItemTypes=Audio&Recursive=true&SortBy=SortName&SortOrder=Ascending&StartIndex={start}&Limit={limit}&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, total, expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => "ext-deezer-song-" + id));
        gateway.VerifyNoOtherCallsAfterAlbumReads();
    }

    [Theory]
    [InlineData("ArtistIds", "one,two", "a,b,c")]
    [InlineData("AlbumArtistIds", "one,two", "a,b")]
    [InlineData("ContributingArtistIds", "one,two", "c")]
    [InlineData("ArtistIds=ext-deezer-artist-one,ext-deezer-artist-two&AlbumArtistIds", "two", "b")]
    [InlineData("ArtistIds=ext-deezer-artist-one&AlbumArtistIds", "two", "")]
    public async Task JellyfinWebArtistQuery_UnionsDistinctIdsAndPreservesRoles(string role, string artists, string expected)
    {
        var gateway = ProviderQueryGateway();
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        var ids = string.Join(',', artists.Split(',').Select(id => "ext-deezer-artist-" + id));
        using var response = await client.GetAsync($"/Items?{role}={ids}&IncludeItemTypes=MusicAlbum&ExcludeItemIds={ids}&SortBy=ProductionYear,SortName&SortOrder=Ascending&Fields=Genres,SortName&Limit=20&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var albums = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        AssertQueryPage(body.RootElement, 0, albums.Length, albums.Select(id => "ext-deezer-album-" + id));
    }

    [Theory]
    [InlineData("Artists/AlbumArtists", 0, 1, "ext-deezer-artist-a")]
    [InlineData("Artists/AlbumArtists", 1, 1, "native-artist")]
    [InlineData("Artists/AlbumArtists", 0, 0, "")]
    [InlineData("Items", 0, 1, "ext-deezer-artist-a")]
    [InlineData("Items", 1, 1, "native-artist")]
    [InlineData("Items", 0, 0, "")]
    public async Task JellyfinFinampArtistSearch_DeduplicatesSortsAndPagesMergedPrefix(string endpoint, int start, int limit, string expected)
    {
        const string native = """{"Id":"native-artist","Name":"Query Beta","Type":"MusicArtist","Future" : { "keep" : [1, true], "text":"Björk" },"ProviderIds":{"MusicBrainzArtist":"same"}}""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Query", 0, 0, It.IsAny<int>(), null))
            .ReturnsAsync(new SearchResult { Artists = [new Artist { Id = "ext-deezer-artist-a", Name = "Query Alpha" }, new Artist { Id = "ext-deezer-artist-a", Name = "Query Alpha" }, new Artist { Id = "ext-deezer-artist-b", Name = "Query Beta" }] });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "Query", It.IsAny<int>())).ReturnsAsync([]);
        using var factory = ProviderQueryFactory(gateway.Object, request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal("0", query["StartIndex"]);
            Assert.True(int.Parse(query["Limit"].ToString()) >= 20);
            return $$"""{"Items":[{{native}}],"TotalRecordCount":1,"StartIndex":0}""";
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/{endpoint}?IncludeItemTypes=MusicArtist&SearchTerm=Query&SortBy=SortName&SortOrder=Ascending&StartIndex={start}&Limit={limit}&Fields=ProviderIds,Genres,SortName&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, 2, expected.Length == 0 ? [] : [expected]);
        if (expected == "native-artist") Assert.Equal(native, body.RootElement.GetProperty("Items")[0].GetRawText());
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Query", 0, 0, It.Is<int>(count => count >= 20), null), Times.Once);
        gateway.Verify(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "Query", It.IsAny<int>()), Times.AtMostOnce);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 1, "b")]
    [InlineData(1, 1, "a")]
    [InlineData(2, 1, "b")]
    [InlineData(0, 0, "")]
    public async Task JellyfinFinampSourcePlaylistQuery_HasStableWindowsWithoutDroppingOccurrences(int start, int limit, string expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "query"))
            .ReturnsAsync([QuerySong("b", "Keep Alpha", 2020, "Rock"), QuerySong("a", "Keep Zulu", 2019, "Jazz"), QuerySong("b", "Keep Alpha", 2020, "Rock")]);
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ParentId=ext-deezer-playlist-query&IncludeItemTypes=Audio&StartIndex={start}&Limit={limit}&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, 3, expected.Length == 0 ? [] : ["ext-deezer-song-" + expected]);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ParentId", "IsFavorite=true", 0, 20)]
    [InlineData("ParentId", "Filters=IsFavorite", 0, 20)]
    [InlineData("ParentId", "IsFavorite=false", 0, 20)]
    [InlineData("AlbumIds", "IsFavorite=true", 0, 20)]
    [InlineData("AlbumIds", "Filters=IsFavorite", 0, 20)]
    [InlineData("AlbumIds", "IsFavorite=false", 0, 20)]
    [InlineData("ParentId", "IsFavorite=true", 1, 1)]
    [InlineData("AlbumIds", "Filters=IsFavorite", 1, 1)]
    [InlineData("ParentId", "Filters=IsFavorite", 0, 0)]
    [InlineData("AlbumIds", "IsFavorite=true", 0, 0)]
    public async Task JellyfinFinampAlbumFavorites_UsesCurrentViewerStateBeforeCounting(string scope, string favorite, int start, int limit)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var saved = QuerySong("saved", "Keep saved", 2020, "Rock");
        var foreign = QuerySong("foreign-track", "Keep foreign", 2020, "Rock");
        saved.AlbumId = foreign.AlbumId = "ext-deezer-album-one";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "saved")).ReturnsAsync(saved);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "foreign-track")).ReturnsAsync(foreign);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { Songs = [saved, foreign] });
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        foreach (var (viewer, song) in new[] { ("fixture", saved), ("foreign", foreign) })
        {
            using var mutation = await client.PostAsync($"/UserFavoriteItems/{song.Id}?ApiKey={viewer}", null);
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        }
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "saved"), Times.Once);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "foreign-track"), Times.Once);
        gateway.VerifyNoOtherCalls();
        gateway.Invocations.Clear();
        await using var db = storage.CreateDbContext();
        var before = await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id).Select(item => item.Revision).ToArrayAsync();
        using var response = await client.GetAsync($"/Items?{scope}=ext-deezer-album-one&IncludeItemTypes=Audio&SearchTerm=Keep&Genres=Rock&Years=2020&{favorite}&StartIndex={start}&Limit={limit}&ApiKey=fixture");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, 1, start > 0 || limit == 0 ? [] :
            ["ext-deezer-song-" + (favorite == "IsFavorite=false" ? "foreign-track" : "saved")]);
        Assert.All(body.RootElement.GetProperty("Items").EnumerateArray(), item =>
            Assert.Equal(favorite != "IsFavorite=false", item.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean()));
        Assert.Equal(before, await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id).Select(item => item.Revision).ToArrayAsync());
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"),
            favorite == "IsFavorite=false" ? Times.Once() : Times.Never());
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinMixedAlbumQuery_PrefersNativeProviderIdentityAndPreservesItsPayload()
    {
        const string native = """{"Id":"native-song","Name":"Keep Alpha","Type":"Audio","ProviderIds":{"deezer":"b"},"Future" : { "keep":true, "text":"Björk" }}""";
        var gateway = ProviderQueryGateway();
        using var factory = ProviderQueryFactory(gateway.Object, request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal("native-album", query["AlbumIds"]);
            Assert.Equal("0", query["StartIndex"]);
            return $$"""{"Items":[{{native}}],"TotalRecordCount":1}""";
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?AlbumIds=ext-deezer-album-one,native-album&IncludeItemTypes=Audio&SortBy=SortName&Fields=ProviderIds&Limit=10&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, 2, ["native-song", "ext-deezer-song-a"]);
        Assert.Equal(native, body.RootElement.GetProperty("Items")[0].GetRawText());
    }

    [Fact]
    public async Task JellyfinWebNativeCatalog_SortsProviderAdditionsBeforePaging()
    {
        var gateway = JellyfinCatalogGateway("album", false);
        using var factory = JellyfinCatalogFactory(gateway.Object, "album",
            """{"Items":[{"Id":"native-child","Name":"Oblivion","Type":"Audio","ParentIndexNumber":1,"IndexNumber":2,"Future":true}],"TotalRecordCount":1}""");
        using var client = factory.CreateClient();
        using var parent = await client.GetAsync("/Users/user-1/Items/native-parent?api_key=fixture-key");
        using var response = await client.GetAsync("/Items?ParentId=native-parent&SortBy=ParentIndexNumber,IndexNumber,SortName&Limit=1&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, 2, ["ext-deezer-song-missing"]);
    }

    [Theory]
    [InlineData("ParentIndexNumber,IndexNumber,SortName", "Ascending", "c,b,a")]
    [InlineData("Name", "Descending", "a,c,b")]
    [InlineData("ProductionYear,PremiereDate,SortName", "Descending", "b,c,a")]
    [InlineData("PremiereDate,AlbumArtist,Album,SortName", "Descending", "b,c,a")]
    [InlineData("Runtime,AlbumArtist,Album,SortName", "Ascending", "b,a,c")]
    [InlineData("Artist,Album,SortName", "Ascending", "c,b,a")]
    [InlineData("AlbumArtist,Album,SortName", "Ascending", "c,b,a")]
    [InlineData("Album,SortName", "Ascending", "c,a,b")]
    [InlineData("ProductionYear,ParentIndexNumber,Name", "Descending,Ascending", "c,b,a")]
    public async Task JellyfinFeishinSongSort_UsesAvailableMetadataAndStableTies(string sort, string order, string expected)
    {
        var a = QuerySong("a", "Zulu", 2019, "Rock");
        a.ReleaseDate = "2019-06-01";
        a.DiscNumber = 2;
        a.Duration = 100;
        a.Album = "Beta";
        a.Artist = "Zulu";
        a.AlbumArtist = "Zulu";
        var b = QuerySong("b", "Alpha", 2020, "Rock");
        b.ReleaseDate = "2020-12-01";
        b.DiscNumber = 1;
        b.Track = 2;
        b.Duration = 50;
        b.Album = "Zulu";
        b.Artist = "Beta";
        b.AlbumArtist = "Beta";
        var c = QuerySong("c", "Beta", 2020, "Rock");
        c.ReleaseDate = "2020-02-01";
        c.DiscNumber = 1;
        c.Track = 1;
        c.Duration = 150;
        c.Album = "Alpha";
        c.Artist = "Alpha";
        c.AlbumArtist = "Alpha";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "sort"))
            .ReturnsAsync(new Album { Songs = [a, b, c] });
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ParentId=ext-deezer-album-sort&IncludeItemTypes=Audio&SortBy={sort}&SortOrder={order}&Limit=20&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, 3, expected.Split(',').Select(id => "ext-deezer-song-" + id));
    }

    [Fact]
    public async Task JellyfinFeishinAlbumQuery_ReconcilesKnownRecordingsAcrossRoutes()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var first = QuerySong("b", "Keep Alpha", 2020, "Rock");
        var copy = QuerySong("copy", "Keep Alpha", 2020, "Jazz");
        first.CanonicalRecordingId = copy.CanonicalRecordingId = Guid.Parse("0199a468-3ac7-799c-8f10-914282c0e622");
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { Songs = [QuerySong("a", "Keep Zulu", 2019, "Jazz"), first] });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "two"))
            .ReturnsAsync(new Album { Songs = [copy, QuerySong("c", "Keep Beta", 2020, "Rock")] });
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?AlbumIds=ext-deezer-album-one,ext-deezer-album-two&IncludeItemTypes=Audio&SortBy=SortName&Limit=10&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, 3, ["ext-deezer-song-b", "ext-deezer-song-c", "ext-deezer-song-a"]);
        using var filtered = await client.GetAsync("/Items?AlbumIds=ext-deezer-album-one,ext-deezer-album-two&IncludeItemTypes=Audio&Genres=Jazz&SortBy=SortName&Limit=10&api_key=fixture-key");
        using var filteredBody = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        AssertQueryPage(filteredBody.RootElement, 0, 2, ["ext-deezer-song-copy", "ext-deezer-song-a"]);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 1, "ext-deezer-album-guest-1", false)]
    [InlineData(1, 1, "ext-deezer-album-guest-2", false)]
    [InlineData(0, 0, "", false)]
    [InlineData(0, 1, "ext-deezer-album-guest-1", true)]
    public async Task JellyfinWebContributingArtistQuery_TotalDoesNotDependOnTheRequestedWindow(int start, int limit, string expected, bool knownPrefix)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-one", Name = "Query Artist" });
        var owned = Enumerable.Range(1, knownPrefix ? 200 : 0).Select(index => new Album
        { Id = "ext-deezer-album-owned-" + index, ArtistId = "ext-deezer-artist-one" }).ToList();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one")).ReturnsAsync(owned);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(owned.Select((album, index) => new Song { Id = "ext-deezer-song-owned-" + index, AlbumId = album.Id })
                .Concat(Enumerable.Range(1, 3).Select(index => new Song
                { Id = "ext-deezer-song-" + index, AlbumId = "ext-deezer-album-guest-" + index })).ToList());
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new Album
            { Id = "ext-deezer-album-" + id, Title = id, Artist = "Other", ArtistId = "ext-deezer-artist-other", Year = 2020 });
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ContributingArtistIds=ext-deezer-artist-one&IncludeItemTypes=MusicAlbum&SortBy=PremiereDate,ProductionYear,SortName&StartIndex={start}&Limit={limit}&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, 3, expected.Length == 0 ? [] : [expected]);
        gateway.Verify(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"), Times.Once);
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"), Times.Once);
        gateway.Verify(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"), Times.Once);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Rock%2C%20alternative", "a")]
    [InlineData("Rock%7CJazz", "b,c")]
    public async Task JellyfinFinampAlbumGenres_UsesPipeDelimitedNamesBeforeFieldSelection(string genres, string expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "genres"))
            .ReturnsAsync(new Album { Songs = [QuerySong("a", "First", 2020, "Rock, alternative"), QuerySong("b", "Second", 2020, "Rock"), QuerySong("c", "Third", 2020, "Jazz")] });
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ParentId=ext-deezer-album-genres&IncludeItemTypes=Audio&Genres={genres}&SortBy=Name&Limit=20&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, expected.Split(',').Length, expected.Split(',').Select(id => "ext-deezer-song-" + id));
        Assert.All(body.RootElement.GetProperty("Items").EnumerateArray(), row => Assert.False(row.TryGetProperty("Genres", out _)));
    }

    [Theory]
    [InlineData("ArtistIds", "", "guest,other,primary")]
    [InlineData("AlbumArtistIds", "", "other,primary")]
    [InlineData("ContributingArtistIds", "", "guest")]
    [InlineData("ArtistIds", "&ExcludeArtistIds=ext-deezer-artist-other", "primary")]
    public async Task JellyfinFeishinArtistSongs_UsesPerformingAndAlbumArtistRelationships(string role, string exclusions, string expected)
    {
        var primary = QuerySong("primary", "Primary", 2020, "Rock");
        primary.AlbumId = "ext-deezer-album-primary";
        var other = QuerySong("other", "Other", 2020, "Rock");
        other.ArtistId = "ext-deezer-artist-other";
        other.AlbumId = primary.AlbumId;
        var guest = QuerySong("guest", "Guest", 2020, "Rock");
        guest.AlbumId = "ext-deezer-album-guest";
        var unrelated = QuerySong("unrelated", "Unrelated", 2020, "Rock");
        unrelated.ArtistId = "ext-deezer-artist-other";
        unrelated.AlbumId = guest.AlbumId;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one")).ReturnsAsync([primary, guest]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync([
                new Album { Id = primary.AlbumId, ArtistId = "ext-deezer-artist-one", Songs = [primary, other] },
                new Album { Id = guest.AlbumId, ArtistId = "ext-deezer-artist-other", Songs = [guest, unrelated] }
            ]);
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?{role}=ext-deezer-artist-one{exclusions}&IncludeItemTypes=Audio&SortBy=SortName&Limit=20&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, 0, expected.Split(',').Length, expected.Split(',').Select(id => "ext-deezer-song-" + id));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 1, "", 4, "alpha-1")]
    [InlineData(1, 1, "", 4, "alpha-2")]
    [InlineData(2, 1, "", 4, "beta-1")]
    [InlineData(0, 0, "", 4, "")]
    [InlineData(0, 10, "&SearchTerm=Alpha%20Two&ExcludeItemIds=ext-deezer-playlist-alpha-1&Years=2020", 1, "alpha-2")]
    public async Task JellyfinFinampCuratorQuery_UnionsAndPagesTheExistingPlaylistSearch(int start, int limit, string filters, int total, string expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        foreach (var curator in new[] { "alpha", "beta" })
            gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), curator, 50))
                .ReturnsAsync([
                    new ExternalPlaylist { Id = "ext-deezer-playlist-" + curator + "-1", ExternalId = curator + "-1", Provider = "deezer", CuratorName = curator,
                        Name = curator == "alpha" ? "Alpha One" : "Beta One", CreatedDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                    new ExternalPlaylist { Id = "ext-deezer-playlist-" + curator + "-2", ExternalId = curator + "-2", Provider = "deezer", CuratorName = curator,
                        Name = curator == "alpha" ? "Alpha Two" : "Beta Two", CreatedDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                    new ExternalPlaylist { Id = "ext-deezer-playlist-unrelated", Provider = "deezer", CuratorName = "other", Name = "Unrelated" }
                ]);
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ArtistIds=ext-deezer-curator-alpha,ext-deezer-curator-beta,ext-deezer-curator-alpha&IncludeItemTypes=MusicAlbum&SortBy=SortName&StartIndex={start}&Limit={limit}{filters}&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryPage(body.RootElement, start, total, expected.Length == 0 ? [] : ["ext-deezer-playlist-" + expected]);
        gateway.Verify(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "alpha", 50), Times.Once);
        gateway.Verify(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "beta", 50), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    private static void AssertQueryPage(JsonElement root, int start, int total, IEnumerable<string> ids)
    {
        Assert.Equal(start, root.GetProperty("StartIndex").GetInt32());
        Assert.Equal(total, root.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(ids, root.GetProperty("Items").EnumerateArray().Select(row => row.GetProperty("Id").GetString()));
        Assert.All(root.GetProperty("Items").EnumerateArray(), row => Assert.Equal(JsonValueKind.String, row.GetProperty("Type").ValueKind));
    }

    private static Song QuerySong(string id, string title, int year, string genre) => new()
    {
        Id = "ext-deezer-song-" + id,
        ExternalProvider = "deezer",
        ExternalId = id,
        Title = title,
        Artist = "Query Artist",
        ArtistId = "ext-deezer-artist-one",
        Year = year,
        Genre = genre,
        Track = id == "a" ? 2 : 1
    };

    private static Mock<IProtocolProviderGateway> ProviderQueryGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new Album
            {
                Id = "ext-deezer-album-" + id,
                Songs = id == "one"
                    ? [QuerySong("a", "Keep Zulu", 2019, "Jazz"), QuerySong("b", "Keep Alpha", 2020, "Rock")]
                    : [QuerySong("b", "Keep Alpha", 2020, "Rock"), QuerySong("c", "Keep Beta", 2020, "Rock")]
            });
        foreach (var artist in new[] { "one", "two" })
        {
            gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", artist))
                .ReturnsAsync(new Artist { Id = "ext-deezer-artist-" + artist, Name = "Query " + artist });
            gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", artist))
                .ReturnsAsync([
                    new Album { Id = "ext-deezer-album-" + (artist == "one" ? "a" : "b"), Title = "Primary " + artist, ArtistId = "ext-deezer-artist-" + artist, Artist = "Query " + artist, Year = 2019 },
                    new Album { Id = "ext-deezer-album-c", Title = "Guest", ArtistId = "ext-deezer-artist-other", Artist = "Other", Year = 2020 }
                ]);
            gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", artist)).ReturnsAsync([]);
        }
        return gateway;
    }

    private ProtocolFactory ProviderQueryFactory(IProtocolProviderGateway gateway, Func<HttpRequestMessage, string>? native = null) =>
        new("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : native != null ? Json(200, native(request)) : throw new InvalidOperationException($"Unexpected fixture path: {request.RequestUri.AbsolutePath}"), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
        });
}

internal static class ProviderQueryVerifications
{
    public static void VerifyNoOtherCallsAfterAlbumReads(this Mock<IProtocolProviderGateway> gateway)
    {
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"), Times.Once);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "two"), Times.AtMostOnce);
        gateway.VerifyNoOtherCalls();
    }
}
