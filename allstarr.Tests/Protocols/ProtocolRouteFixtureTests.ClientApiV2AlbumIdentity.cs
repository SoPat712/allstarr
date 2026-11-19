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
    private const string SameLibraryAlbum =
        """{"Id":"album1","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Stored artist","ChildCount":1,"ProductionYear":2020,"ImageTags":{"Primary":"albumtag"}}""";
    private const string DeluxeLibraryAlbum =
        """{"Id":"album1","Name":"Stored album (Deluxe Edition)","Type":"MusicAlbum","AlbumArtist":"Stored artist","ChildCount":15,"ProductionYear":2021}""";
    private const string SecondLibraryAlbum =
        """{"Id":"album2","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Stored artist","ChildCount":1}""";
    private const string NamesakeLibraryAlbum =
        """{"Id":"album1","Name":"Stored album","Type":"MusicAlbum","AlbumArtist":"Someone else","ChildCount":1}""";

    [Theory]
    // The same artist, title, edition and song count is the same album.
    [InlineData(SameLibraryAlbum, false, true, "album", null, 0)]
    // Another edition is only offered, with what differs.
    [InlineData(DeluxeLibraryAlbum, false, true, null, "Same title and artist. 15 songs there, 1 here.", 1)]
    // Unless a song of this album is the listener's own song on it.
    [InlineData(DeluxeLibraryAlbum, true, true, "songs", null, 0)]
    // Two library albums that both fit are for a person to tell apart.
    [InlineData(SameLibraryAlbum + "," + SecondLibraryAlbum, false, true, null, "Same album. Your library has it more than once.", 2)]
    // Another artist's album of that name, or none at all, says nothing.
    [InlineData(NamesakeLibraryAlbum, false, true, null, null, 0)]
    [InlineData("", false, true, null, null, 0)]
    // An album a search stored has no songs or song count yet; the album itself is read before anything is said.
    [InlineData(SameLibraryAlbum, false, false, "album", null, 0)]
    [InlineData(DeluxeLibraryAlbum, false, false, null, "Same title and artist. 15 songs there, 1 here.", 1)]
    [InlineData(DeluxeLibraryAlbum, true, false, "songs", null, 0)]
    public async Task ClientApiV2_AServiceAlbumSaysWhichLibraryAlbumItIsOrCouldBe(string libraryAlbums, bool ownsSong, bool storedWithSongs,
        string? by, string? reason, int offered)
    {
        Song Stored()
        {
            var song = V2BatchSong("known");
            song.Isrc = "USAT21900001";
            return song;
        }
        var gateway = V2BatchGateway();
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>())).ReturnsAsync(Stored);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>())).ReturnsAsync(Stored);
        Album Service(bool withSongs) => new()
        {
            Id = "ext-deezer-album-7",
            Title = "Stored album",
            Artist = "Stored artist",
            ArtistId = "ext-deezer-artist-9",
            Year = 2020,
            SongCount = withSongs ? 1 : null,
            Songs = withSongs ? [Stored()] : []
        };
        gateway.Setup(item => item.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7")).ReturnsAsync(() => Service(storedWithSongs));
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7")).ReturnsAsync(() => Service(true));
        var library = new Mock<ILibraryIndexService> { DefaultValue = DefaultValue.Mock };
        library.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, IReadOnlyList<LibraryRecordingLookup> lookups, CancellationToken _) =>
                (IReadOnlyDictionary<string, string>)lookups.ToDictionary(lookup => lookup.PublicId, _ => "native1", StringComparer.Ordinal));
        var searches = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/UserViews") return Json(200, """{"Items":[]}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query.GetValueOrDefault("includeItemTypes") != "MusicAlbum") return Json(200, V2LibrarySong);
            if (query.TryGetValue("searchTerm", out var term)) searches.Add(term.ToString());
            return Json(200, $$"""{"Items":[{{libraryAlbums}}]}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            if (!ownsSong) return;
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true, Stored());

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
            Assert.Equal(by == "songs" ? 1 : null, same["ownedSongs"]?.GetValue<int>());
            Assert.Equal(by == "songs" ? 15 : 1, same["songCount"]!.GetValue<int>());
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
        Assert.Single(full["tracks"]!.AsArray());
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
