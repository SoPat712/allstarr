using System.Net;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    private const string ChosenLines = "[00:00.10]I'm a working bitch\n[00:00.80]Last line";

    [Fact]
    public async Task ChosenLyrics_AnAdministratorsChoiceGoesIntoTheSongsFileAndToTheMediaServer()
    {
        var administrator = await AddPromotionUserAsync(true);
        var song = await LibrarySongAsync("[00:01.00]Song - Singer\n[00:02.00]Old line");
        var server = new MediaServer();
        var files = ChosenFiles(server, enabled: true);

        await files.RequestAsync(administrator, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await RunChosenAsync(files))!.Kind);

        using (var file = TagLib.File.Create(song)) Assert.Equal(ChosenLines, file.Tag.Lyrics);
        Assert.StartsWith("[00:00.100]<00:00.100>I'm <00:00.200>a ", TagLibManagedTagFileMutator.ReadWordLyrics(song));
        // The media server gets the word times too, in place of the lines it took from the file earlier.
        var sent = Assert.Single(server.Requests);
        Assert.Equal((HttpMethod.Post, "/Audio/item-1/Lyrics?fileName=lyrics.lrc", TagLibManagedTagFileMutator.ReadWordLyrics(song)),
            (sent.Method, sent.PathAndQuery, sent.Body));
        Assert.Contains("Token=\"key\"", sent.Authorization);
        // What the file held is kept, and nothing is left beside the song.
        var kept = Assert.Single(Directory.GetFiles(Path.Combine(directory, "data", "replaced-lyrics")));
        Assert.Contains("Old line", await File.ReadAllTextAsync(kept));
        Assert.Equal(new[] { song }, Directory.GetFiles(Path.GetDirectoryName(song)!));
        await using (var db = factory.CreateDbContext())
            Assert.Equal("item-1", (await db.AuditEvents.SingleAsync(item => item.Category == "lyrics" && item.Action == "file.written")).SubjectId);

        // The same choice again leaves the file as it is.
        var written = File.GetLastWriteTimeUtc(song);
        await files.RequestAsync(administrator, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await RunChosenAsync(files))!.Kind);
        Assert.Equal(written, File.GetLastWriteTimeUtc(song));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "data", "replaced-lyrics")));
    }

    [Fact]
    public async Task ChosenLyrics_OnlyAnAdministratorsChoiceIsWrittenAndOnlyWhileTheHouseholdAllowsIt()
    {
        var administrator = await AddPromotionUserAsync(true);
        var listener = await AddPromotionUserAsync(false);
        var song = await LibrarySongAsync("[00:02.00]Old line");
        var server = new MediaServer();

        var allowed = ChosenFiles(server, enabled: true);
        await allowed.RequestAsync(listener, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        // The library's own version is what the file holds already; a song outside the library has no file.
        await allowed.RequestAsync(administrator, "item-1", song, recordingId, "backend", WordTimed(), 1000);
        await allowed.RequestAsync(administrator, "item-1", null, recordingId, "qqlyrics:1", WordTimed(), 1000);
        Assert.Null(await RunChosenAsync(allowed));
        await ChosenFiles(server, enabled: false).RequestAsync(administrator, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        Assert.Null(await RunChosenAsync(allowed));

        // Turned off between the choice and the write: the file stays as it was.
        await allowed.RequestAsync(administrator, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        var refused = await RunChosenAsync(ChosenFiles(server, enabled: false));
        Assert.Equal((DurableJobCompletionKind.Failed, "lyrics_file_not_allowed"), (refused!.Kind, refused.ErrorCode));
        // A file outside every music folder Allstarr knows is never touched.
        await allowed.RequestAsync(administrator, "item-1", Path.Combine(directory, "source.wav"), recordingId, "qqlyrics:1", WordTimed(), 1000);
        var outside = await RunChosenAsync(allowed);
        Assert.Equal((DurableJobCompletionKind.Failed, "lyrics_file_unreachable"), (outside!.Kind, outside.ErrorCode));

        using (var file = TagLib.File.Create(song)) Assert.Equal("[00:02.00]Old line", file.Tag.Lyrics);
        Assert.Empty(server.Requests);

        // The page where a version is chosen says beforehand whether the file will be written.
        Assert.True(await allowed.WritesAsync(administrator, song));
        Assert.False(await allowed.WritesAsync(listener, song));
        Assert.False(await allowed.WritesAsync(administrator, Path.Combine(directory, "source.wav")));
        Assert.False(await ChosenFiles(server, enabled: false).WritesAsync(administrator, song));
    }

    [Fact]
    public async Task ChosenLyrics_AMediaServerThatIsAwayIsHandedTheLyricsLater()
    {
        var administrator = await AddPromotionUserAsync(true);
        var song = await LibrarySongAsync("[00:02.00]Old line");
        var server = new MediaServer { Status = HttpStatusCode.ServiceUnavailable };
        var files = ChosenFiles(server, enabled: true);

        await files.RequestAsync(administrator, "item-1", song, recordingId, "qqlyrics:1", WordTimed(), 1000);
        var claim = await queue.ClaimNextAsync("lyrics-test", [ChosenLyricsFiles.JobType]);
        var away = await files.ExecuteAsync(new(claim!, services), default);
        Assert.Equal((DurableJobCompletionKind.Retry, "lyrics_file_server_unavailable"), (away.Kind, away.ErrorCode));
        using (var file = TagLib.File.Create(song)) Assert.Equal(ChosenLines, file.Tag.Lyrics);

        server.Status = HttpStatusCode.OK;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await files.ExecuteAsync(new(claim!, services), default)).Kind);
        Assert.Equal(2, server.Requests.Count);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "data", "replaced-lyrics")));
    }

    [Fact]
    public async Task ChosenLyrics_PlainLyricsTakeThePlaceOfTheTimedOnesTheMediaServerHolds()
    {
        var server = new MediaServer();
        await new JellyfinItemLyrics(new HttpClient(server), new JellyfinSettings { Url = "http://media.invalid/base", ApiKey = "key" })
            .ReplaceAsync("item-1", "First line\nSecond line", default);
        Assert.Equal(new[] { (HttpMethod.Delete, "/base/Audio/item-1/Lyrics"), (HttpMethod.Post, "/base/Audio/item-1/Lyrics?fileName=lyrics.txt") },
            server.Requests.Select(request => (request.Method, request.PathAndQuery)).ToArray());
    }

    [Fact]
    public async Task ChosenLyrics_ALineTimedVersionGoesToTheMediaServerAsItsLines()
    {
        var administrator = await AddPromotionUserAsync(true);
        var song = await LibrarySongAsync("[00:02.00]Old line");
        var server = new MediaServer();
        var files = ChosenFiles(server, enabled: true);

        await files.RequestAsync(administrator, "item-1", song, recordingId, "lrclib:1", new LyricsInfo { SyncedLyrics = ChosenLines }, 1000);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await RunChosenAsync(files))!.Kind);
        Assert.Equal(ChosenLines, Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task AddedSong_TheMediaServerIsHandedTheWordsItsFileHolds()
    {
        var song = await LibrarySongAsync("[00:00.10]I'm a working bitch\n[00:00.80]Last line");
        var server = new MediaServer();
        var added = new AddedSongLyrics(new LibraryFileLyrics(roots),
            new JellyfinItemLyrics(new HttpClient(server), new JellyfinSettings { Url = "http://media.invalid", ApiKey = "key" }), NullLogger<AddedSongLyrics>.Instance);

        // Line times only: the media server reads those from the file on its own.
        await added.HandOverAsync("item-1", song, default);
        Assert.Empty(server.Requests);

        var words = ChosenLyricsFiles.Tags(LyricsTiming.SidecarContent(WordTimed(), 1000)!)["wordlyrics"];
        new TagLibManagedTagFileMutator().Apply(song, new Dictionary<string, string> { ["wordlyrics"] = words }, default);
        await added.HandOverAsync("item-1", song, default);
        var sent = Assert.Single(server.Requests);
        Assert.Equal((HttpMethod.Post, "/Audio/item-1/Lyrics?fileName=lyrics.lrc", words), (sent.Method, sent.PathAndQuery, sent.Body));
        Assert.Contains("<00:00.200>a ", words);

        // A media server that is away does not stop the add, and a lyrics file beside the song is the media server's to read.
        server.Status = HttpStatusCode.ServiceUnavailable;
        await added.HandOverAsync("item-1", song, default);
        await File.WriteAllTextAsync(Path.ChangeExtension(song, ".lrc"), words);
        await added.HandOverAsync("item-1", song, default);
        await added.HandOverAsync("item-1", Path.Combine(directory, "source.wav"), default);
        Assert.Equal(2, server.Requests.Count);
    }

    private ChosenLyricsFiles ChosenFiles(MediaServer server, bool enabled)
    {
        var household = new Mock<IDurableRuntimeSettings>();
        household.Setup(item => item.GetAsync(ChosenLyricsFiles.SettingKey, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveRuntimeSetting(ChosenLyricsFiles.SettingKey, RuntimeSettingValueType.Boolean, enabled, enabled ? "true" : "false",
                RuntimeSettingOrigin.Durable, 1, "test", null, null));
        return new(household.Object, factory, queue, new LibraryFileLyrics(roots), new TagLibManagedTagFileMutator(), new PhysicalManagedFileOperations(),
            new JellyfinItemLyrics(new HttpClient(server), new JellyfinSettings { Url = "http://media.invalid", ApiKey = "key" }),
            new StorageOptions { DataDirectory = Path.Combine(directory, "data") }, TimeProvider.System, NullLogger<ChosenLyricsFiles>.Instance);
    }

    private async Task<DurableJobCompletion?> RunChosenAsync(ChosenLyricsFiles files)
    {
        var claim = await queue.ClaimNextAsync("lyrics-test", [ChosenLyricsFiles.JobType]);
        if (claim == null) return null;
        var completion = await files.ExecuteAsync(new(claim, services), default);
        await queue.CompleteAsync(claim, completion);
        return completion;
    }

    // A song in the music folder, holding the lyrics it was given when it was added.
    private async Task<string> LibrarySongAsync(string lyrics)
    {
        var path = Path.Combine(directory, "library", "music", "Song.wav");
        await WaveSourceAsync();
        File.Copy(Path.Combine(directory, "source.wav"), path);
        new TagLibManagedTagFileMutator().Apply(path, new Dictionary<string, string> { ["lyrics"] = lyrics }, default);
        return path;
    }

    private static LyricsInfo WordTimed() => new()
    {
        SyncedLyrics = "structured timing",
        TimedLines =
        [
            new("I'm a working bitch", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500),
                [new("I'm ", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)), new("a ", TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100)),
                 new("work", TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)), new("ing ", TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100)),
                 new("bitch", TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100))]),
            new("Last line", TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(100),
                [new("Last ", TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(50)), new("line", TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(50))])
        ]
    };

    private sealed class MediaServer : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly List<(HttpMethod Method, string PathAndQuery, string? Body, string? Authorization)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Authorization", out var values) ? string.Join(' ', values) : null));
            return new(Status);
        }
    }
}
