using System.Net;
using System.Text.Json.Nodes;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string OwnAlbum = """{"Id":"album1","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Stored artist","ChildCount":9,"ProductionYear":2020,"ImageTags":{"Primary":"albumtag"}}""";
    private const string OwnDeluxeAlbum = """{"Id":"album1","Name":"Stored album (Deluxe Edition)","Type":"MusicAlbum","AlbumArtist":"Stored artist","ProductionYear":2021}""";
    private const string OwnSecondAlbum = """{"Id":"album2","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Stored artist"}""";
    private const string NamesakeAlbum = """{"Id":"album1","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Someone else"}""";
    // The service album has "Stored song" (3:20) and "Second song" (3:00).
    private const string FirstSong = """{"Id":"s1","Type":"Audio","Name":"Stored song","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":2010000000}""";
    private const string SecondSong = """{"Id":"s2","Type":"Audio","Name":"Second song","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":1800000000}""";
    private const string BonusSong = """{"Id":"s3","Type":"Audio","Name":"Bonus song","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":1500000000}""";
    private const string LiveSong = """{"Id":"s4","Type":"Audio","Name":"Stored song (Live)","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":2000000000}""";
    private const string LongerSong = """{"Id":"s5","Type":"Audio","Name":"Stored song","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":2100000000}""";
    private const string OtherSong = """{"Id":"s6","Type":"Audio","Name":"Another song","ArtistItems":[{"Name":"Stored artist","Id":"artist1"}],"RunTimeTicks":2000000000}""";
    private const string NoSongsMatch = "Same title and artist. None of its songs match by title and length.";

    [Theory]
    // Every song of the shorter album is on the other, by title and length: one song owned of two, the whole album,
    // and a longer edition that holds both of this album's songs.
    [InlineData(OwnAlbum, FirstSong, false, true, "titles", 1, 1, null, 0)]
    [InlineData(OwnAlbum, FirstSong + "," + SecondSong, false, true, "titles", 2, 2, null, 0)]
    [InlineData(OwnDeluxeAlbum, FirstSong + "," + SecondSong + "," + BonusSong, false, true, "titles", 2, 3, null, 0)]
    // An album of that name with other songs is another album, and so is one that only shares some of them.
    [InlineData(OwnAlbum, OtherSong, false, true, null, 0, 0, NoSongsMatch, 1)]
    [InlineData(OwnAlbum, FirstSong + "," + OtherSong, false, true, null, 0, 0, "Same title and artist. Shares 1 of its 2 songs.", 1)]
    // A live take or a different length is another song.
    [InlineData(OwnAlbum, LiveSong, false, true, null, 0, 0, NoSongsMatch, 1)]
    [InlineData(OwnAlbum, LongerSong, false, true, null, 0, 0, NoSongsMatch, 1)]
    // A song of this album that is the listener's own by recording settles it whatever the titles say.
    [InlineData(OwnDeluxeAlbum, OtherSong + "," + BonusSong, true, true, "songs", 1, 2, null, 0)]
    // Two library albums that both hold it are for a person to tell apart.
    [InlineData(OwnAlbum + "," + OwnSecondAlbum, FirstSong, false, true, null, 0, 0, "Same album. Your library has it more than once.", 2)]
    // Another artist's album of that name, or none at all, says nothing.
    [InlineData(NamesakeAlbum, FirstSong, false, true, null, 0, 0, null, 0)]
    [InlineData("", "", false, true, null, 0, 0, null, 0)]
    // An album a search stored has no songs yet; the album itself is read before anything is said.
    [InlineData(OwnAlbum, FirstSong, false, false, "titles", 1, 1, null, 0)]
    [InlineData(OwnDeluxeAlbum, OtherSong + "," + BonusSong, true, false, "songs", 1, 2, null, 0)]
    public async Task ClientApiV2_AServiceAlbumSaysWhichLibraryAlbumItIsOrCouldBe(string libraryAlbums, string librarySongs, bool ownsByRecording,
        bool storedWithSongs, string? by, int ownedSongs, int songCount, string? reason, int offered)
    {
        Song Stored(string id)
        {
            var song = V2BatchSong(id);
            song.Isrc = id == "known" ? "USAT21900001" : "USAT21900002";
            if (id != "known") (song.Title, song.Duration) = ("Second song", 180);
            return song;
        }
        var gateway = V2BatchGateway();
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => Stored(id));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => Stored(id));
        Album Service(bool withSongs) => new()
        {
            Id = "ext-deezer-album-7",
            Title = "Stored album",
            Artist = "Stored artist",
            ArtistId = "ext-deezer-artist-9",
            Year = 2020,
            SongCount = withSongs ? 2 : null,
            Songs = withSongs ? [Stored("known"), Stored("second")] : []
        };
        gateway.Setup(item => item.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7")).ReturnsAsync(() => Service(storedWithSongs));
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7")).ReturnsAsync(() => Service(true));
        var library = new Mock<ILibraryIndexService> { DefaultValue = DefaultValue.Mock };
        library.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, IReadOnlyList<LibraryRecordingLookup> lookups, CancellationToken _) =>
                (IReadOnlyDictionary<string, string>)lookups.Where(lookup => lookup.PublicId.EndsWith("-known", StringComparison.Ordinal))
                    .ToDictionary(lookup => lookup.PublicId, _ => "native1", StringComparer.Ordinal));
        var searches = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/UserViews") return Json(200, """{"Items":[]}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            // A library album's own songs, then a song read by id, then the album search.
            if (query.ContainsKey("parentId") && query.GetValueOrDefault("includeItemTypes") == "Audio") return Json(200, $$"""{"Items":[{{librarySongs}}]}""");
            if (query.GetValueOrDefault("includeItemTypes") != "MusicAlbum") return Json(200, V2LibrarySong);
            if (query.TryGetValue("searchTerm", out var term)) searches.Add(term.ToString());
            return Json(200, $$"""{"Items":[{{libraryAlbums}}]}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            if (!ownsByRecording) return;
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true, Stored("known"));
        await SeedV2DetailsAsync(factory, "listener", "second", personal: true, Stored("second"));

        var path = $"/allstarr/v2/albums/{V2Ids.Album("ext-deezer-album-7")}";
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("Stored album", Assert.Single(searches.Distinct()));

        if (by == null) Assert.False(body.ContainsKey("libraryAlbum"), text);
        else
        {
            var same = body["libraryAlbum"]!;
            Assert.Equal(by, same["by"]!.GetValue<string>());
            Assert.Equal(V2Ids.Album("album1"), same["id"]!.GetValue<string>());
            Assert.Equal("album1", same["libraryId"]!.GetValue<string>());
            Assert.Equal(ownedSongs, same["ownedSongs"]!.GetValue<int>());
            Assert.Equal(songCount, same["songCount"]!.GetValue<int>());
            Assert.False(body.ContainsKey("couldBe"), text);
        }
        if (reason == null) Assert.False(body.ContainsKey("couldBe"), text);
        else
        {
            var could = body["couldBe"]!.AsArray();
            Assert.Equal(offered, could.Count);
            Assert.All(could, item => Assert.Equal(reason, item!["reason"]!.GetValue<string>()));
            Assert.Equal("album1", could[0]!["libraryId"]!.GetValue<string>());
            Assert.False(body.ContainsKey("libraryAlbum"), text);
        }
        // The album itself is asked for only when its songs are needed: for a library album of that name, or the songs.
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"),
            storedWithSongs ? Times.Never() : Times.Once());

        // Asking for the songs too gives the same answer about the album.
        using var withTracks = await client.GetAsync(path + "?include=tracks");
        var full = JsonNode.Parse(await withTracks.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(2, full["tracks"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(body["libraryAlbum"], full["libraryAlbum"]), full.ToJsonString());
        Assert.True(JsonNode.DeepEquals(body["couldBe"], full["couldBe"]), full.ToJsonString());

        // A library album never points at itself.
        using var own = await client.GetAsync("/allstarr/v2/albums/album1");
        Assert.Equal(libraryAlbums.Contains("\"album1\"") ? HttpStatusCode.OK : HttpStatusCode.NotFound, own.StatusCode);
        if (own.StatusCode == HttpStatusCode.OK)
        {
            var mine = JsonNode.Parse(await own.Content.ReadAsStringAsync())!.AsObject();
            Assert.False(mine.ContainsKey("libraryAlbum"));
            Assert.False(mine.ContainsKey("couldBe"));
        }
        // The media server lists its first albums for an id it cannot read; that is not the album asked for.
        using var unknown = await client.GetAsync("/allstarr/v2/albums/not-an-album");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
