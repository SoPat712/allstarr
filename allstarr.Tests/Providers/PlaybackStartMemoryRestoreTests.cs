using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Providers;
using allstarr.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class PlaybackStartMemoryRestoreTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly Guid _account = Guid.CreateVersion7();
    private static readonly ProviderExternalResourceId Track = new("deezer", ProviderResourceKind.Track, "sample");
    private static readonly ProviderMediaFormat Flac = new("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16);
    private static readonly ProviderMediaFormat HighResolution = new("audio/flac", "flac", "flac", sampleRate: 96000, bitDepth: 24);

    [Fact]
    public async Task ARestartAnswersTheSameStartTimeAndFormatAsBeforeIt()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var store = Store(factory);
        var before = Health();
        foreach (var (seconds, media) in new[] { (1, Flac), (9, (ProviderMediaFormat?)null), (2, Flac) })
            await PlayAsync(store, () => before.ObservePlayback(Context(), Track, ProviderAudioQuality.Lossless, null,
                TimeSpan.FromSeconds(seconds), media));
        // A start served from the server's own copy teaches the memory nothing, before a restart or after it.
        await PlayAsync(store, () => PlaybackStartTimings.Current!.Select("deezer", true, "Shared"));
        await PlayAsync(store, () => before.ObservePlayback(Context(), Track, ProviderAudioQuality.HighResolution,
            new(ProviderErrorKind.TransientFailure), TimeSpan.FromSeconds(1)));
        // A source check's start counts as much as a listener's.
        before.ObservePlaybackStart(Context(), ProviderAudioQuality.Lossless, TimeSpan.FromSeconds(4), HighResolution);
        var check = new ProviderCtsDiagnosticResult { Succeeded = true, ProviderId = "deezer", ProviderAccountId = _account, MeasuredAt = _time.GetUtcNow() };
        check.PlaybackStart = new("deezer", _account, 4, ProviderAudioQuality.Lossless, _time.GetUtcNow(), 4000, HighResolution);
        await AddAsync(factory, PlaybackStartMemoryRestore.CheckCategory, null, JsonSerializer.Serialize(check));
        var learned = before.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless);
        Assert.Equal(new PlaybackStartObservation(TimeSpan.FromSeconds(3), HighResolution), learned);

        _time.Advance(TimeSpan.FromMinutes(10));
        var after = Health();
        Assert.Null(after.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless));
        Assert.Equal(4, await Restore(factory, after).RestoreAsync(default));
        Assert.Equal(learned, after.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless));
        Assert.Null(after.GetPlaybackStart(Context(), ProviderAudioQuality.HighResolution));
        Assert.Null(after.GetPlaybackStart(Context(revision: 5), ProviderAudioQuality.Lossless));
    }

    [Fact]
    public async Task RestoredStartsKeepTheirAgeAndExpiredOnesAreNotRestored()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var store = Store(factory);
        var before = Health();
        await PlayAsync(store, () => before.ObservePlayback(Context(), Track, ProviderAudioQuality.Lossless, null, TimeSpan.FromSeconds(5), Flac));
        _time.Advance(TimeSpan.FromHours(2));
        await PlayAsync(store, () => before.ObservePlayback(Context(), Track, ProviderAudioQuality.Lossless, null, TimeSpan.FromSeconds(1), Flac));

        // Eleven hours after the first start, it has under an hour left.
        _time.Advance(TimeSpan.FromHours(9));
        var after = Health();
        Assert.Equal(2, await Restore(factory, after).RestoreAsync(default));
        Assert.Equal(TimeSpan.FromSeconds(3), after.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless)!.MedianStart);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(1), after.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless)!.MedianStart);

        var later = Health();
        Assert.Equal(1, await Restore(factory, later).RestoreAsync(default));
        Assert.Equal(TimeSpan.FromSeconds(1), later.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless)!.MedianStart);
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Null(later.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless));
        Assert.Equal(0, await Restore(factory, Health()).RestoreAsync(default));
    }

    [Fact]
    public async Task RowsThatDoNotReadAreSkippedAndTheRestStillRestore()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var at = _time.GetUtcNow().ToString("O");
        string Start(string provider, string quality, string media) =>
            $$"""{"Starts":[{"Provider":{{provider}},"Account":"{{_account}}","Revision":4,"Quality":"{{quality}}","At":"{{at}}","ElapsedMs":2000,"Media":{{media}}}]}""";
        foreach (var json in new[]
        {
            "{not json",
            "[]",
            Start("\"deezer\"", "Loud", "null"),
            Start("null", "Lossless", "null"),
            Start("\"deezer\"", "Lossless", """{"MimeType":"audio/flac","Container":"flac","Codec":"flac","Bitrate":-1}"""),
            """{"Starts":[null]}""",
            Start("\"deezer\"", "Lossless", """{"MimeType":"audio/flac","Container":"flac","Codec":"flac","SampleRate":44100,"BitDepth":16}""")
        })
            await AddAsync(factory, PlaybackStartTimingStore.Category, "recent", json);
        await AddAsync(factory, PlaybackStartMemoryRestore.CheckCategory, null, """{"Succeeded":false,"PlaybackStart":{"Provider":"deezer"}}""");
        _time.Advance(TimeSpan.FromMinutes(1));
        var after = Health();
        Assert.Equal(1, await Restore(factory, after).RestoreAsync(default));
        Assert.Equal(new PlaybackStartObservation(TimeSpan.FromSeconds(2), Flac), after.GetPlaybackStart(Context(), ProviderAudioQuality.Lossless));
    }

    private async Task PlayAsync(PlaybackStartTimingStore store, Action play)
    {
        using (var timing = new PlaybackStartTimings(_time))
        {
            timing.Enter();
            timing.Enable();
            play();
            await store.RecordAsync(timing.Snapshot("completed", complete: true));
        }
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task AddAsync(ProviderCheckServiceTests.Factory factory, string category, string? subject, string json)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = category,
            Action = "recorded",
            Outcome = "recorded",
            CorrelationId = "restore-test",
            SubjectType = subject == null ? null : category,
            SubjectId = subject,
            DetailsJson = json,
            CreatedAt = _time.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }

    private PlaybackStartTimingStore Store(ProviderCheckServiceTests.Factory factory) =>
        new(factory, _time, NullLogger<PlaybackStartTimingStore>.Instance);

    private PlaybackStartMemoryRestore Restore(ProviderCheckServiceTests.Factory factory, ProviderRuntimeHealth health) =>
        new(factory, health, new DurableStorageState(new StorageOptions()), _time, NullLogger<PlaybackStartMemoryRestore>.Instance);

    private ProviderRuntimeHealth Health() => new(clock: new Clock(_time));

    private ProviderExecutionContext Context(long revision = 4) => new(
        new ProviderActorContext(ProviderActorKind.Administrator, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new ProviderBackendPrincipal("jellyfin", "fixture", "admin")), "deezer",
        new ProviderAccountContext(_account, "deezer", ProviderAccountScope.Shared, revision),
        new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]),
        "test", "restore-test", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class Clock(TimeProvider time) : IPlatformClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
}
