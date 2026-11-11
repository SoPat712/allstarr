using System.Net;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_SearchStreamAcceptsSingleLetterDeduplicatesAndTerminates()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "a", 20,
                true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext context, string query, int limit, bool songs, bool albums, bool artists, bool playlists,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", new SearchResult
                {
                    Songs = [new Song { Id = "ext-deezer-song-one", ExternalProvider = "deezer", ExternalId = "one",
                        Title = "Visible", Artist = "Artist", Album = "Release", Duration = 200 }],
                    Albums = [new Album { Id = "ext-deezer-album-owned", Title = "Owned", Artist = "Artist" }],
                    Artists = [new Artist { Id = "ext-deezer-artist-owned", Name = "Owned artist" }]
                }, [], token);
                await write("qobuz", new SearchResult
                {
                    Songs = [new Song { Id = "ext-qobuz-song-two", ExternalProvider = "qobuz", ExternalId = "two",
                        Title = "Visible", Artist = "Artist", Album = "Release", Duration = 200 }]
                }, [], token);
                return (IReadOnlyList<string>)["slow"];
            });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/System/Info/Public" => Json(200, """{"Id":"native-server"}"""),
            "/Users/listener/Items" => Json(200, """{"Items":[{"Id":"native-album","Type":"MusicAlbum","Name":"Owned","AlbumArtist":"Artist","Artists":["Artist"]},{"Id":"native-artist","Type":"MusicArtist","Name":"Owned artist"}]}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/search?q=a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("deezer", lines[0]["provider"]!.GetValue<string>());
        // The library's same-named album and artist no longer hide Deezer's; every item names its source.
        var deezer = lines[0]["items"]!.AsArray();
        Assert.Equal(["ext-deezer-album-owned", "ext-deezer-artist-owned", "ext-deezer-song-one"],
            deezer.Select(row => row!["Id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        Assert.All(deezer, row => Assert.Equal("deezer", row!["source"]!.GetValue<string>()));
        Assert.All(deezer, row => Assert.False(string.IsNullOrWhiteSpace(row!["sourceName"]!.GetValue<string>())));
        var item = deezer.Single(row => row!["Id"]!.GetValue<string>() == "ext-deezer-song-one")!;
        Assert.Equal("Audio", item["Type"]!.GetValue<string>());
        Assert.Equal(SearchRelevanceRanker.ScoreSong("a", "Visible", "Artist", "Release", durationSeconds: 200),
            item["score"]!.GetValue<double>());
        Assert.Null(item["Score"]);
        Assert.Null(item["id"]);
        Assert.Empty(lines[1]["items"]!.AsArray());
        Assert.True(lines[2]["done"]!.GetValue<bool>());
        Assert.Equal("slow", Assert.Single(lines[2]["late"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_SearchStreamScoresAndStablyRanksEveryKindTogether()
    {
        const string query = "say now";
        var songs = new[] { "Brick By Brick", "Supermarket", "Met my match" }.Select((title, index) => new Song
        {
            Id = $"ext-deezer-song-{index}",
            ExternalProvider = "deezer",
            ExternalId = index.ToString(),
            Title = title,
            Artist = "Say Now",
            Duration = 200
        }).ToList();
        var album = new Album { Id = "ext-deezer-album-release", Title = "Release", Artist = "Say Now" };
        var artists = new[] { "Now", "Say When", "Say Now" }.Select((name, index) =>
            new Artist { Id = $"ext-deezer-artist-{index}", Name = name }).ToList();
        var playlist = new ExternalPlaylist { Id = "ext-deezer-playlist-list", Name = "Say Now", CuratorName = "Curator", Provider = "deezer" };
        var rows = await ReadSearchStreamRowsAsync(new() { Songs = songs, Albums = [album], Artists = artists }, [playlist], query);
        var scores = rows.Select(row => row["score"]!.GetValue<double>()).ToArray();
        Assert.Equal(scores.OrderDescending(), scores);
        Assert.All(rows, row =>
        {
            Assert.True(row["score"] is JsonValue value && value.TryGetValue<double>(out _));
            Assert.Null(row["Score"]);
        });
        foreach (var song in songs)
            Assert.Equal(SearchRelevanceRanker.ScoreSong(query, song),
                rows.Single(row => row["Id"]!.GetValue<string>() == song.Id)["score"]!.GetValue<double>());
        Assert.Equal(SearchRelevanceRanker.ScoreAlbum(query, album), rows.Single(row => row["Id"]!.GetValue<string>() == album.Id)["score"]!.GetValue<double>());
        Assert.Equal(SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName),
            rows.Single(row => row["Id"]!.GetValue<string>() == playlist.Id)["score"]!.GetValue<double>());
        foreach (var artist in artists)
            Assert.Equal(SearchRelevanceRanker.Score(query, artist.Name),
                rows.Single(row => row["Id"]!.GetValue<string>() == artist.Id)["score"]!.GetValue<double>());
        Assert.Equal(songs.Select(song => song.Id), rows.Where(row => row["Type"]!.GetValue<string>() == "Audio")
            .Select(row => row["Id"]!.GetValue<string>()));
        var lastSong = Array.FindLastIndex(rows, row => row["Type"]!.GetValue<string>() == "Audio");
        Assert.All(artists.Take(2), artist => Assert.True(lastSong < Array.FindIndex(rows,
            row => row["Id"]!.GetValue<string>() == artist.Id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ranked")]
    public async Task ClientApi_SearchStreamCapsEachKindIncludingPlaylistAlbumRows(string? shape)
    {
        var result = new SearchResult
        {
            Songs = Enumerable.Range(0, 4).Select(index => new Song
            {
                Id = $"ext-deezer-song-{index}",
                ExternalProvider = "deezer",
                ExternalId = index.ToString(),
                Title = $"Track {index}",
                Artist = "Artist",
                Duration = 200
            }).ToList(),
            Albums = Enumerable.Range(0, 4).Select(index => new Album
            { Id = $"ext-deezer-album-{index}", Title = $"Album {index}", Artist = "Artist" }).ToList(),
            Artists = Enumerable.Range(0, 4).Select(index => new Artist
            { Id = $"ext-deezer-artist-{index}", Name = $"Artist {index}" }).ToList()
        };
        var playlists = Enumerable.Range(0, 4).Select(index => new ExternalPlaylist
        { Id = $"ext-deezer-playlist-{index}", Name = $"Playlist {index}", Provider = "deezer" }).ToArray();
        var rows = await ReadSearchStreamRowsAsync(result, playlists, "a", 2, shape);
        Assert.Equal(8, rows.Length);
        foreach (var kind in new[] { "song", "album", "artist", "playlist" })
            Assert.Equal(2, rows.Count(row => row["Id"]!.GetValue<string>().StartsWith($"ext-deezer-{kind}-", StringComparison.Ordinal)));
        Assert.All(rows.Where(row => row["Id"]!.GetValue<string>().Contains("-playlist-", StringComparison.Ordinal)),
            row => Assert.Equal("MusicAlbum", row["Type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ClientApi_SearchStreamRankedShapeUsesCompleteSectionsAndAnExactArtistTop()
    {
        var artist = new Artist { Id = "ext-deezer-artist-exact", Name = "Say Now" };
        var lines = await ReadSearchStreamLinesAsync(new() { Artists = [artist] }, [], "say now", 20, "ranked");
        Assert.NotEmpty(lines);
        Assert.Equal(Enumerable.Range(1, lines.Length), lines.Select(line => line["revision"]!.GetValue<int>()));
        Assert.All(lines, line =>
        {
            Assert.Null(line["provider"]);
            Assert.NotNull(line["items"]);
        });
        Assert.All(lines.Where(line => line["top"] != null), line =>
        {
            Assert.Equal(artist.Id, Assert.Single(line["items"]!.AsArray())!["Id"]!.GetValue<string>());
            Assert.Equal("deezer", Assert.Single(line["providers"]!.AsArray())!.GetValue<string>());
            Assert.Equal(artist.Id, line["top"]!["Id"]!.GetValue<string>());
            Assert.Equal(artist.Id, Assert.Single(line["sections"]!["artists"]!.AsArray())!["Id"]!.GetValue<string>());
        });
        Assert.Equal(artist.Id, lines[^1]["top"]!["Id"]!.GetValue<string>());
        Assert.True(lines[^1]["done"]!.GetValue<bool>());
        Assert.Empty(lines[^1]["late"]!.AsArray());
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("ranked", 40)]
    public async Task ClientApi_SearchStreamOmittedLimitKeepsTheShapeSpecificDefault(string? shape, int expected)
    {
        var lines = await ReadSearchStreamLinesAsync(new(), [], "a", expected, shape, omitLimit: true);
        Assert.NotEmpty(lines);
        Assert.True(lines[^1]["done"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ClientApi_SearchStreamRankedIncludesLibraryArtistTopSongsAndAlbums()
    {
        const string query = "juice wrld";
        var libraryArtist = new { Id = "lib-artist-juice", Name = "Juice WRLD", Type = "MusicArtist" };
        var libraryAlbum = new { Id = "lib-album-goodbye", Name = "Goodbye & Good Riddance", AlbumArtist = "Juice WRLD", Artists = new[] { "Juice WRLD" }, Type = "MusicAlbum" };
        var librarySong = new { Id = "lib-song-lucid", Name = "Lucid Dreams", Artist = "Juice WRLD", Album = "Goodbye & Good Riddance", RunTimeTicks = 2390000000L, Type = "Audio" };
        var libraryItemsJson = System.Text.Json.JsonSerializer.Serialize(new { Items = new object[] { libraryArtist, libraryAlbum, librarySong } });

        var lookalikes = new SearchResult
        {
            Artists =
            [
                new Artist { Id = "ext-artist-same", Name = "Juice WRLD" },
                new Artist { Id = "ext-artist-baby", Name = "BABY juice WRLD [A]" },
                new Artist { Id = "ext-artist-999", Name = "Juice WRLD 999" },
                new Artist { Id = "ext-artist-type", Name = "Juice WRLD Type Beat" },
                new Artist { Id = "ext-artist-dj", Name = "DJ Juice Wrld" }
            ],
            Albums =
            [
                new Album { Id = "ext-album-tribute", Title = "Juice WRLD Tribute", Artist = "Tribute Band" }
            ],
            Songs =
            [
                new Song { Id = "ext-song-tribute", Title = "Juice WRLD Tribute Song", Artist = "Tribute Band", Duration = 200 }
            ]
        };

        var lines = await ReadSearchStreamLinesAsync(lookalikes, [], query, 20, "ranked", nativeItemsJson: libraryItemsJson);
        Assert.NotEmpty(lines);
        var final = lines[^1];
        Assert.True(final["done"]!.GetValue<bool>());

        Assert.Equal("lib-artist-juice", final["top"]!["Id"]!.GetValue<string>());
        Assert.Equal(100, final["top"]!["score"]!.GetValue<double>());

        var artistsSection = final["sections"]!["artists"]!.AsArray();
        Assert.Equal("lib-artist-juice", artistsSection[0]!["Id"]!.GetValue<string>());
        Assert.True(artistsSection.Count > 1);
        // A service artist with the library artist's name is its own row, labelled with where it came from.
        var sameName = Assert.Single(artistsSection, item => item!["Id"]!.GetValue<string>() == "ext-artist-same")!;
        Assert.Equal("deezer", sameName["source"]!.GetValue<string>());
        Assert.Equal("jellyfin", artistsSection[0]!["source"]!.GetValue<string>());
        Assert.Equal("Jellyfin", artistsSection[0]!["sourceName"]!.GetValue<string>());
        Assert.All(final["items"]!.AsArray(), item => Assert.False(string.IsNullOrWhiteSpace(item!["sourceName"]?.GetValue<string>())));

        var albumsSection = final["sections"]!["albums"]!.AsArray();
        Assert.Contains(albumsSection, item => item!["Id"]!.GetValue<string>() == "lib-album-goodbye");

        var songsSection = final["sections"]!["songs"]!.AsArray();
        Assert.Contains(songsSection, item => item!["Id"]!.GetValue<string>() == "lib-song-lucid");

        var items = final["items"]!.AsArray();
        Assert.Contains(items, item => item!["Id"]!.GetValue<string>() == "lib-artist-juice");
        Assert.Contains(items, item => item!["Id"]!.GetValue<string>() == "lib-album-goodbye");
        Assert.Contains(items, item => item!["Id"]!.GetValue<string>() == "lib-song-lucid");
        var scores = items.Select(item => item!["score"]!.GetValue<double>()).ToArray();
        Assert.Equal(scores.OrderDescending(), scores);
    }

    [Fact]
    public async Task ClientApi_SearchStreamRankedSlowLibraryDoesNotDelayFirstExternalSnapshot()
    {
        var releaseLibrary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lookalikes = new SearchResult
        {
            Artists = [new Artist { Id = "ext-artist-fast", Name = "Fast External" }]
        };

        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(It.IsAny<allstarr.Core.Capabilities.ProviderCapabilityKind>()))
            .Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "fast", 20, true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool _, bool _, bool _, bool _,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", lookalikes, [], token);
                await write("qobuz", new SearchResult(), [], token);
                return (IReadOnlyList<string>)[];
            });

        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        using var factory = ClientApiFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/System/Info/Public") return Json(200, """{"Id":"native-server"}""");
            if (request.RequestUri.AbsolutePath == "/Users/listener/Items")
            {
                try { releaseLibrary.Task.Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
                return Json(200, """{"Items":[{"Id":"lib-artist-slow","Name":"Fast External","Type":"MusicArtist"}]}""");
            }
            return Json(404, "{}");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });

        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/search?q=fast&limit=20&shape=ranked", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new System.IO.StreamReader(stream);

        var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(firstLine);
        var firstSnapshot = JsonNode.Parse(firstLine)!;
        Assert.Equal(1, firstSnapshot["revision"]!.GetValue<int>());
        Assert.False(firstSnapshot["done"]!.GetValue<bool>());
        Assert.Equal("ext-artist-fast", firstSnapshot["top"]!["Id"]!.GetValue<string>());

        releaseLibrary.TrySetResult();

        var secondLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(secondLine);
        var secondSnapshot = JsonNode.Parse(secondLine)!;
        Assert.Equal(2, secondSnapshot["revision"]!.GetValue<int>());
        Assert.Equal("lib-artist-slow", secondSnapshot["top"]!["Id"]!.GetValue<string>());
        Assert.True(secondSnapshot["done"]!.GetValue<bool>());
    }

    private static async Task<JsonNode[]> ReadSearchStreamRowsAsync(SearchResult result,
        IReadOnlyList<ExternalPlaylist> playlists, string query, int limit = 20, string? shape = null)
    {
        var lines = await ReadSearchStreamLinesAsync(result, playlists, query, limit, shape);
        return shape == "ranked"
            ? lines[^1]["sections"]!.AsObject().SelectMany(section => section.Value!.AsArray()).OfType<JsonNode>().ToArray()
            : lines.Where(line => line["items"] != null).SelectMany(line => line["items"]!.AsArray()).OfType<JsonNode>().ToArray();
    }

    private static async Task<JsonNode[]> ReadSearchStreamLinesAsync(SearchResult result,
        IReadOnlyList<ExternalPlaylist> playlists, string query, int limit, string? shape, bool omitLimit = false,
        string? nativeItemsJson = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), query, limit, true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool _, bool _, bool _, bool _,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", result, playlists, token);
                return (IReadOnlyList<string>)[];
            });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/System/Info/Public" => Json(200, """{"Id":"native-server"}"""),
            "/Users/listener/Items" => Json(200, nativeItemsJson ?? """{"Items":[]}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync($"/allstarr/v1/search?q={Uri.EscapeDataString(query)}" +
            (omitLimit ? "" : $"&limit={limit}") +
            (shape == null ? "" : $"&shape={shape}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        gateway.Verify(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), query, limit, true, true, true, true,
            It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
    }
}
