using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class RankedSearchRecordingRowsTests
{
    [Fact]
    public async Task ServiceCopiesWithTheSameIsrcAreOneRowAndLookalikesStayApart()
    {
        await using var search = new Search(["songs"]);
        await search.AddAsync("deezer", Song("ext-deezer-song-1", "deezer", "Walk", "Walk", 152, "GBAHT2100001"));
        // Song 3 has Walk's title, artist and length on another album without an ISRC: not proven, so its own row.
        var copy = Song("ext-tidal-song-2", "tidal", "Walk", "vertigo", 153, "GBAHT2100001");
        var lookalike = Song("ext-tidal-song-3", "tidal", "Walk", "One Foot In Front Of The Other", 152);
        var live = Song("ext-tidal-song-4", "tidal", "Walk (Live)", "Walk", 160, "GBAHT2100009");
        await search.AddAsync("tidal", copy, lookalike, live);

        var songs = (await search.FinalAsync())["sections"]!["songs"]!.AsArray();

        Assert.Equal(["ext-deezer-song-1", "ext-tidal-song-3", "ext-tidal-song-4"], songs.Select(Id).Order(StringComparer.Ordinal).ToArray());
        var walk = songs.Single(row => Id(row) == "ext-deezer-song-1")!;
        Assert.Equal([("deezer", "ext-deezer-song-1"), ("tidal", "ext-tidal-song-2")], Copies(walk));
        Assert.Equal("deezer", walk["source"]!.GetValue<string>());
        Assert.All(songs.Where(row => Id(row) != "ext-deezer-song-1"), row => Assert.Null(row!["alsoOn"]));
    }

    [Fact]
    public async Task LibraryCopyTakesThePlaceOfEveryServiceRowProvenToBeIt()
    {
        await using var search = new Search(["songs"]);
        await search.AddAsync("deezer", Song("ext-deezer-song-1", "deezer", "Walk", "Walk", 152));
        // Allstarr matched this copy to the library song, though nothing in its metadata ties it to Deezer's.
        await search.AddAsync("tidal", Song("ext-tidal-song-2", "tidal", "Walk", "vertigo", 153) with { LibraryCopy = "lib-walk" });
        var native = new NativeMusicItem<object>(MusicItemKind.Song, "lib-walk", "Walk", "Griff", new object())
        { AlbumText = "Walk", DurationSeconds = 152 };
        await search.AddLibraryAsync(new RankedSearchRow(new JsonObject
        {
            ["Id"] = "lib-walk",
            ["Name"] = "Walk",
            ["score"] = 100d,
            ["source"] = "jellyfin",
            ["sourceName"] = "Jellyfin"
        }, "songs", Native: native));

        var final = await search.FinalAsync();

        var walk = Assert.Single(final["sections"]!["songs"]!.AsArray())!;
        Assert.Equal("lib-walk", Id(walk));
        Assert.Equal([("jellyfin", "lib-walk"), ("deezer", "ext-deezer-song-1"), ("tidal", "ext-tidal-song-2")], Copies(walk));
        Assert.Equal("lib-walk", Id(final["top"]));
        Assert.Equal(["lib-walk"], final["items"]!.AsArray().Select(Id).ToArray());
    }

    [Fact]
    public async Task AnotherServicesCopyOfAReleaseJoinsItAndLibraryAlbumsStayApart()
    {
        const string title = "I just wanna make a lot of money, be my own housewife";
        await using var search = new Search(["albums"]);
        await search.AddAsync("apple", Album("ext-apple-album-1", "apple", title + " - Single"));
        await search.AddAsync("deezer", Album("ext-deezer-album-2", "deezer", title), Album("ext-deezer-album-3", "deezer", title + " (Remixes)"));
        var native = new NativeMusicItem<object>(MusicItemKind.Album, "lib-album", title, "AT Musica", new object());
        await search.AddLibraryAsync(new RankedSearchRow(new JsonObject
        {
            ["Id"] = "lib-album",
            ["Name"] = title,
            ["score"] = 100d,
            ["source"] = "jellyfin",
            ["sourceName"] = "Jellyfin"
        }, "albums", Native: native));

        var albums = (await search.FinalAsync())["sections"]!["albums"]!.AsArray();

        Assert.Equal(["ext-apple-album-1", "ext-deezer-album-3", "lib-album"], albums.Select(Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([("apple", "ext-apple-album-1"), ("deezer", "ext-deezer-album-2")], Copies(albums.Single(row => Id(row) == "ext-apple-album-1")!));
    }

    private static RankedSearchRow Song(string id, string source, string title, string album, int duration, string? isrc = null) =>
        Row(id, source, "songs", new Song { Id = id, Title = title, Artist = "Griff", Album = album, Duration = duration, Isrc = isrc }, null);

    private static RankedSearchRow Album(string id, string source, string title) =>
        Row(id, source, "albums", null, new Album { Id = id, Title = title, Artist = "AT Musica", Year = 2025, SongCount = 1 });

    private static RankedSearchRow Row(string id, string source, string section, Song? song, Album? album) => new(new JsonObject
    {
        ["Id"] = id,
        ["Name"] = song?.Title ?? album!.Title,
        ["score"] = 100d,
        ["source"] = source,
        ["sourceName"] = source
    }, section, Song: song, Album: album);

    private static string Id(JsonNode? row) => row!["Id"]!.GetValue<string>();

    private static (string, string)[] Copies(JsonNode row) => row["alsoOn"]!.AsArray()
        .Select(entry => (entry!["source"]!.GetValue<string>(), entry["id"]!.GetValue<string>())).ToArray();

    private sealed class Search : IAsyncDisposable
    {
        private readonly Channel<JsonNode> emitted = Channel.CreateUnbounded<JsonNode>();
        private readonly RankedSearchSnapshots snapshots;
        private readonly Task running;

        internal Search(IReadOnlyList<string> sections)
        {
            snapshots = new("walk griff", 20, sections, "deezer", new ManualTimeProvider(),
                (value, _) => { emitted.Writer.TryWrite(JsonSerializer.SerializeToNode(value)!); return Task.CompletedTask; });
            running = snapshots.RunAsync(CancellationToken.None);
        }

        internal Task AddAsync(string provider, params RankedSearchRow[] rows) => snapshots.AddAsync(provider, rows, CancellationToken.None);

        internal Task AddLibraryAsync(params RankedSearchRow[] rows) => snapshots.AddLibraryAsync(rows, CancellationToken.None);

        internal async Task<JsonNode> FinalAsync()
        {
            snapshots.Complete([]);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            JsonNode? last = null;
            while (emitted.Reader.TryRead(out var value)) last = value;
            Assert.True(last!["done"]!.GetValue<bool>());
            return last;
        }

        public async ValueTask DisposeAsync()
        {
            snapshots.Complete([]);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
