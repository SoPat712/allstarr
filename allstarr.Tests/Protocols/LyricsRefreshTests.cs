using System.Text.Json;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientEvents;
using allstarr.Core.Intelligence;
using allstarr.Services.Common;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsRefreshTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private readonly Clock clock = new();
    private LyricsRefreshStore store = null!;
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid recording = Guid.NewGuid();
    private ProtocolExecutionContext Context => new(ProtocolKind.Jellyfin, "fixture", "listener",
        new AllstarrPrincipal(owner, "jellyfin", "fixture", "listener", "Listener", false),
        "lyrics-refresh", clock.UtcNow.AddMinutes(5), default);
    private Song Song => new()
    {
        Id = "ext-allstarr-song-" + recording,
        CanonicalRecordingId = recording,
        ExternalProvider = "allstarr",
        ExternalId = recording.ToString("D"),
        Title = "Song",
        Artist = "Artist",
        Duration = 120
    };

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        await using var db = await factory.CreateDbContextAsync();
        db.Users.Add(new() { Id = owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", Enabled = true });
        db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, Title = "Song", DurationMilliseconds = 120_000 });
        await db.SaveChangesAsync();
        store = new(factory, clock);
    }

    [Theory]
    [InlineData("plain", "line", 120, true)]
    [InlineData("line", "word", 120, true)]
    [InlineData("word", "line", 120, false)]
    [InlineData("line", "line", 120, false)]
    [InlineData("line", "word", 30, false)]
    [InlineData("line", "word", 140, false)]
    public async Task UpgradesRequireStrictlyBetterScoreAndDurationFit(string before, string after, int end, bool upgraded)
    {
        var original = Lyrics(before);
        original.Source = "old";
        await store.ObserveAsync(Context, Song, original, 120_000);
        await using var read = await factory.CreateDbContextAsync();
        var state = await read.RecordingLyricsRefreshes.AsNoTracking().SingleAsync();
        var candidate = Lyrics(after, end);
        candidate.Source = "new";
        var result = await store.CompleteAsync(state, candidate, "daily", default);
        Assert.Equal(upgraded, result);
        await using var db = await factory.CreateDbContextAsync();
        var saved = await db.RecordingLyricsRefreshes.SingleAsync();
        Assert.Equal(upgraded ? "new" : "old", saved.SourceId);
        Assert.Equal(upgraded, saved.PreviousJson != null);
        if (upgraded)
        {
            Assert.Equal("old", LyricsRefreshStore.Read(saved.PreviousJson!).Lyrics!.Source);
            Assert.Contains("duration_fits", saved.Reason);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinAddedDuringLookupPreventsUpgrade(bool personal)
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RecordingLyricsChoices.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                OwnerUserId = personal ? owner : null,
                SourceId = "old",
                Revision = 4
            });
            await db.SaveChangesAsync();
        }
        Assert.False(await store.CompleteAsync(state, Lyrics("word"), "daily", default));
        Assert.Empty(await store.DueAsync(null, null, 25, default));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(4, (await verify.RecordingLyricsChoices.SingleAsync()).Revision);
        Assert.Equal("old", (await verify.RecordingLyricsRefreshes.SingleAsync()).SourceId);
    }

    [Fact]
    public async Task ObservedSourcesUpgradeOnlyWhenBetterAndFitting()
    {
        await store.ObserveAsync(Context, Song, Lyrics("plain"), 120_000);
        await store.ObserveAsync(Context, Song, Lyrics("word", 30), 120_000);
        Assert.NotNull((await store.CurrentAsync(owner, recording, default))!.Lyrics!.PlainLyrics);
        await store.ObserveAsync(Context, Song, Lyrics("word"), 120_000);
        var current = await store.CurrentAsync(owner, recording, default);
        Assert.NotNull(current!.Lyrics!.TimedLines);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.RecordingLyricsRefreshes.SingleAsync();
        Assert.NotNull(LyricsRefreshStore.Read(row.PreviousJson!).Lyrics!.PlainLyrics);
        Assert.Contains("duration_fits", row.Reason);
    }

    [Fact]
    public async Task PersonalPinsDoNotBlockAnotherListenersAutomaticUpgrade()
    {
        var other = Guid.NewGuid();
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = other, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "other", Enabled = true });
            db.RecordingLyricsChoices.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, OwnerUserId = owner, SourceId = "old", Revision = 4 });
            await db.SaveChangesAsync();
        }
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "other",
            new AllstarrPrincipal(other, "jellyfin", "fixture", "other", "Other listener", false),
            "other-listener", clock.UtcNow.AddMinutes(5), default);
        await store.ObserveAsync(context, Song, Lyrics("line"), 120_000);
        var state = Assert.Single(await store.DueAsync(null, null, 25, default));
        Assert.Equal(other, state.OwnerUserId);
        Assert.True(await store.CompleteAsync(state, Lyrics("word"), "daily", default));
        await using var verify = await factory.CreateDbContextAsync();
        var pin = await verify.RecordingLyricsChoices.SingleAsync();
        Assert.Equal(("old", 4L), (pin.SourceId, pin.Revision));
        Assert.Equal("old", (await store.CurrentAsync(owner, recording, default))!.Lyrics!.Source);
        Assert.Equal("new", (await store.CurrentAsync(other, recording, default))!.Lyrics!.Source);
    }

    [Fact]
    public async Task MissesBackOffAndSourceRecoveryBypassesSongCooldown()
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        await store.CompleteAsync(state, null, "daily", default);
        Assert.Empty(await store.DueAsync(null, null, 25, default));
        Assert.Single(await store.DueAsync("amll", null, 25, default));
        clock.UtcNow = clock.UtcNow.AddDays(7);
        state = (await store.DueAsync(null, null, 25, default)).Single();
        await store.CompleteAsync(state, null, "daily", default);
        clock.UtcNow = clock.UtcNow.AddDays(7);
        Assert.Empty(await store.DueAsync(null, null, 25, default));
        clock.UtcNow = clock.UtcNow.AddDays(7);
        Assert.Single(await store.DueAsync(null, null, 25, default));
    }

    [Fact]
    public async Task PreviousChoiceSurvivesRestartAndCanBeRestored()
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        Assert.True(await store.CompleteAsync(state, Lyrics("word"), "source recovered", default));
        var restarted = new LyricsRefreshStore(factory, clock);
        Assert.True(await restarted.RevertAsync(owner, recording, default));
        Assert.Equal("old", (await restarted.CurrentAsync(owner, recording, default))!.Lyrics!.Source);
    }

    [Fact]
    public async Task HealthyWordLyricsAndSourcesUnableToImproveAreSkipped()
    {
        await store.ObserveAsync(Context, Song, Lyrics("word"), 120_000);
        Assert.Empty(await store.DueAsync(null, null, 25, default));
        Assert.Empty(await store.DueAsync("amll", null, 25, default));
    }

    [Fact]
    public async Task RefreshCapsSongsPacesReadsAndInvalidatesOnlyChangedSongs()
    {
        var recordings = new[] { recording, Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.AddRange(recordings.Skip(1).Select(id => new CanonicalRecordingRecord
            { Id = id, CreatedByUserId = owner, Title = "Song", DurationMilliseconds = 120_000 }));
            await db.SaveChangesAsync();
        }
        foreach (var id in recordings)
        {
            var song = Song;
            song.CanonicalRecordingId = id;
            song.Id = "ext-allstarr-song-" + id;
            song.ExternalId = id.ToString("D");
            await store.ObserveAsync(Context, song, Lyrics("line"), 120_000);
        }
        var time = new ManualTimeProvider(clock.UtcNow);
        var resolver = new Mock<IProtocolLyricsResolver>();
        resolver.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(),
            It.Is<ProtocolLyricsSources>(source => source.ReadOnly && source.OnlySourceId == "lyricsplus")))
            .ReturnsAsync([new ProtocolLyricsCandidate("lyricsplus", Lyrics("word"))]);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus"]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(Policy());
        var events = new Mock<IClientEventChanges>();
        var changes = new LyricsSearchChanges(time);
        using var index = new LyricsSearchIndex(database.StorageOptions);
        var service = new LyricsRefreshService(store, factory, resolver.Object, providers.Object, policies.Object, time,
            new() { MaximumSongs = 2, PaceMilliseconds = 1000 }, NullLogger<LyricsRefreshService>.Instance, events.Object, changes, index);
        var refresh = service.RefreshAsync(new(), "test-refresh", default);
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimerAsync());
        Assert.Single(resolver.Invocations);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await refresh;
        Assert.Equal(new LyricsRefreshResult(2, 2, 2), result);
        Assert.Equal(2, resolver.Invocations.Count);
        Assert.Equal(2, changes.TakeRecordings().Count);
        Assert.Equal(2, (await index.SnapshotAsync(Context)).Count(document => document.Format == "word"));
        events.Verify(item => item.LyricsAsync(It.IsAny<Guid>(), owner, true), Times.Exactly(2));
        await using var verify = await factory.CreateDbContextAsync();
        var audit = await verify.AuditEvents.SingleAsync(item => item.Action == "automatic.upgraded");
        Assert.Equal(2, JsonDocument.Parse(audit.DetailsJson).RootElement.GetProperty("wordSynced").GetInt32());
        Assert.Single(await store.DueAsync(null, null, 25, default));
        var noChange = await service.RefreshAsync(new("spotify"), "no-change", default);
        Assert.Equal(new LyricsRefreshResult(0, 0, 0), noChange);
        Assert.Equal(1, await verify.AuditEvents.CountAsync(item => item.Action == "automatic.upgraded"));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task NextLyricsRequestUsesTheUpgradeEvenWhenAnOlderProviderCacheAnswers(ProtocolKind kind)
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        Assert.True(await store.CompleteAsync(state, Lyrics("word"), "daily", default));
        var context = new ProtocolExecutionContext(kind, "fixture", "listener",
            new AllstarrPrincipal(owner, kind.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
            "next-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus"]);
        providers.Setup(item => item.GetLyricsAsync(context, "lyricsplus", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Song", It.IsAny<IReadOnlyList<string>>(), "", 120, null)).ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "lyricsplus", ProviderLyricsFormat.LineTimed, "[01:59.000]Older lines"));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, refreshStore: store);
        var rich = await resolver.GetRichLyricsAsync(context, Song, recording.ToString("N"));
        Assert.Equal("word", rich!.Format);
        Assert.Equal("new", rich.Source.Id);
    }

    [Fact]
    public async Task SidecarReplacementRequiresMeasuredDurationAndKeepsPreviousBytes()
    {
        var path = Path.Combine(database.StorageOptions.DataDirectory, "Song.flac");
        var sidecar = Path.ChangeExtension(path, ".lrc");
        const string original = "[00:01.000]Old line\n[01:59.000]Last line";
        await File.WriteAllTextAsync(sidecar, original);
        Assert.False(await LyricsRefreshSidecars.ReplaceAsync(path, Song, Lyrics("word"), 240_000, default));
        Assert.Equal(original, await File.ReadAllTextAsync(sidecar));
        Assert.True(await LyricsRefreshSidecars.ReplaceAsync(path, Song, Lyrics("word"), 120_000, default));
        Assert.Equal(original, await File.ReadAllTextAsync(Directory.GetFiles(database.StorageOptions.DataDirectory, "*.previous-*").Single()));
        Assert.Contains("<00:02.000>", await File.ReadAllTextAsync(sidecar));
    }

    [Fact]
    public async Task SavedSidecarUsesTheMeasuredFileEndForInferredWordEnds()
    {
        var path = Path.Combine(database.StorageOptions.DataDirectory, "Song.flac");
        await File.WriteAllBytesAsync(path, []);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:01.000]Old line\n[01:59.000]Last line");
        var candidate = new LyricsInfo
        {
            Source = "amll",
            Duration = 124,
            SyncedLyrics = "[00:01.000]First words\n[01:15.000]Last words",
            TimedLines = [new("First words", TimeSpan.FromSeconds(1), segments:
                    [new("First ", TimeSpan.FromSeconds(1)), new("words", TimeSpan.FromSeconds(2))]),
                new("Last words", TimeSpan.FromSeconds(75), segments:
                    [new("Last ", TimeSpan.FromSeconds(75)), new("words", TimeSpan.FromSeconds(76))])]
        };
        Assert.True(await LyricsRefreshSidecars.ReplaceAsync(path, Song, candidate, 120_000, default));
        var saved = await allstarr.Core.Downloads.KeptLyricsReader.ReadStoredSidecarAsync(Song, path, default);
        Assert.NotNull(saved);
        Assert.Contains("duration_fits", LyricsQuality.Evaluate(saved, Song, 120_000)!.Reasons);
        Assert.Equal(124, candidate.Duration);
    }

    [Fact]
    public async Task MostPlayedThenRecentlyPlayedSongsComeFirst()
    {
        var frequent = Guid.NewGuid();
        var recent = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.AddRange(new CanonicalRecordingRecord { Id = frequent, CreatedByUserId = owner, Title = "Frequent" },
                new CanonicalRecordingRecord { Id = recent, CreatedByUserId = owner, Title = "Recent" });
            foreach (var (id, age) in new[] { (frequent, 3), (frequent, 2), (recent, 0), (recording, 1) })
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    CanonicalRecordingId = id,
                    Protocol = "jellyfin",
                    BackendInstanceId = "fixture",
                    OccurrenceKey = Guid.NewGuid().ToString("N"),
                    State = ListeningEventState.Completed,
                    ListenedAt = clock.UtcNow.AddDays(-age),
                    TrackReference = id.ToString("D")
                });
            await db.SaveChangesAsync();
        }
        foreach (var id in new[] { recording, frequent, recent })
        {
            var song = Song;
            song.CanonicalRecordingId = id;
            await store.ObserveAsync(Context, song, Lyrics("line"), 120_000);
        }
        Assert.Equal(new[] { frequent, recent }, (await store.DueAsync(null, null, 2, default)).Select(item => item.CanonicalRecordingId));
    }

    [Theory]
    [InlineData(ProviderUseMode.Off)]
    [InlineData(ProviderUseMode.ManualOnly)]
    public async Task StoredUpgradeStopsServingWhenItsSourceLeavesAutomaticUse(ProviderUseMode mode)
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        Assert.True(await store.CompleteAsync((await store.DueAsync(null, null, 25, default)).Single(), Lyrics("word"), "daily", default));
        var policy = Policy() with { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("new", mode) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var changedStore = new LyricsRefreshStore(factory, clock, policies.Object);
        Assert.Null(await changedStore.RetainedAsync(Context, recording));
    }

    [Fact]
    public async Task StoredClientDetailsUseTheUpgradedChoiceWithoutFetching()
    {
        var upgraded = Lyrics("word");
        upgraded.Source = "lyricsplus";
        var result = await StoredItemLyrics.ReadAsync(Mock.Of<IApplicationCache>(), Song, recording, null, null, Policy(),
            "https://lyrics.example.test", Context, [], [], default, automaticLyrics: upgraded);
        Assert.Equal(new ItemDetailsLyrics(true, "word", "lyricsplus"), result);
    }

    [Fact]
    public async Task LyricsRequestKeepsTheExistingAutomaticVersionWhenNewCacheTimingDoesNotFit()
    {
        var original = Lyrics("line");
        original.Source = "lyricsplus";
        await store.ObserveAsync(Context, Song, original, 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        Assert.False(await store.CompleteAsync(state, Lyrics("word", 30), "daily", default));
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus"]);
        providers.Setup(item => item.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), "lyricsplus", It.IsAny<string>(),
            ProviderLyricsFormat.LineTimed, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lyricsplus", ProviderLyricsFormat.WordTimed,
                "[00:01.000]Wrong timing", timedLines: Lyrics("word", 30).TimedLines));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, refreshStore: store);
        Assert.Equal("line", (await resolver.GetRichLyricsAsync(Context, Song, recording.ToString("N")))!.Format);
        providers.VerifyAll();
    }

    [Fact]
    public async Task ADisabledAutomaticSourceCanBeReplacedByTheCurrentFallbackBaseline()
    {
        await store.ObserveAsync(Context, Song, Lyrics("word"), 120_000);
        var policy = Policy() with { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("new", ProviderUseMode.Off) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var changedStore = new LyricsRefreshStore(factory, clock, policies.Object);
        await changedStore.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await changedStore.DueAsync(null, null, 25, default)).Single();
        Assert.Equal("old", state.SourceId);
        Assert.Equal("new", LyricsRefreshStore.Read(state.PreviousJson!).Lyrics!.Source);
        var replacement = Lyrics("word");
        replacement.Source = "another";
        Assert.True(await changedStore.CompleteAsync(state, replacement, "another source", default));
    }

    [Fact]
    public void IndexedDisplayEndTimesCannotCreateDurationFitForABaseline()
    {
        var rich = new RichLyrics(new("backend", "Library"), "line", [new(1000, 120_000, "Short excerpt", "lead", [])]);
        var baseline = LyricsRefreshStore.FromIndexedLines(rich, Song);
        Assert.Contains("duration_mismatch", LyricsQuality.Evaluate(baseline, Song, 120_000)!.Reasons);
        var words = rich with { Format = "word", Lines = [rich.Lines[0] with { Words = [new(1000, 120_000, "Short excerpt")] }] };
        var wordBaseline = LyricsRefreshStore.FromIndexedLines(words, Song);
        Assert.NotNull(wordBaseline);
        Assert.Contains("duration_mismatch", LyricsQuality.Evaluate(wordBaseline, Song, 120_000)!.Reasons);
        Assert.Equal("word", RichLyricsMapper.Map(wordBaseline)!.Format);
    }

    [Fact]
    public async Task IndexedWordsSurviveStoredRefreshAndClientProjection()
    {
        var rich = new RichLyrics(new("lrcred", "Fixture"), "word",
            [new(1000, 3000, "café 世界", "lead", [new(1000, 2000, "café "), new(2000, 3000, "世界")]),
             new(1500, 2500, "echo", "background", [new(1500, 2500, "echo")])]);
        var baseline = LyricsRefreshStore.FromIndexedLines(rich, Song);
        Assert.NotNull(baseline);
        await store.ObserveAsync(Context, Song, baseline, 120_000);
        var restored = (await store.CurrentAsync(owner, recording, default))!.Lyrics!;
        Assert.Contains("duration_mismatch", LyricsQuality.Evaluate(restored, Song, 120_000)!.Reasons);
        using var response = JsonDocument.Parse(new allstarr.Core.Protocols.Jellyfin.JellyfinLyricsProtocolAdapter().Shape(restored).Body);
        var lines = response.RootElement.GetProperty("Lyrics");
        Assert.Equal(2, lines.GetArrayLength());
        var line = lines[0];
        Assert.Equal("café 世界", line.GetProperty("Text").GetString());
        Assert.Equal(2, line.GetProperty("Cues").GetArrayLength());
        Assert.Equal(5, line.GetProperty("Cues")[1].GetProperty("Position").GetInt32());
        Assert.Equal(20_000_000, line.GetProperty("Cues")[1].GetProperty("Start").GetInt64());
        Assert.Equal("echo", lines[1].GetProperty("Text").GetString());
        Assert.Equal(15_000_000, lines[1].GetProperty("Start").GetInt64());
        var backgroundWord = Assert.Single(lines[1].GetProperty("Cues").EnumerateArray());
        Assert.Equal(0, backgroundWord.GetProperty("Position").GetInt32());
        Assert.Equal(4, backgroundWord.GetProperty("EndPosition").GetInt32());
        Assert.Equal(15_000_000, backgroundWord.GetProperty("Start").GetInt64());
        Assert.Equal(25_000_000, backgroundWord.GetProperty("End").GetInt64());
    }

    [Fact]
    public async Task APolicyCheckWithoutImprovingSourcesStillStartsTheCooldown()
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns([]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(Policy());
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        var service = new LyricsRefreshService(store, factory, resolver.Object, providers.Object, policies.Object,
            new ManualTimeProvider(clock.UtcNow), new(), NullLogger<LyricsRefreshService>.Instance);
        Assert.Equal(new LyricsRefreshResult(1, 0, 0), await service.RefreshAsync(new(), "no-sources", default));
        Assert.Empty(await store.DueAsync(null, null, 25, default));
        resolver.VerifyNoOtherCalls();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task ASourceTriggerStillChoosesBetterWordsAlreadyStoredByAnotherSource()
    {
        await store.ObserveAsync(Context, Song, Lyrics("plain"), 120_000);
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(),
            It.Is<ProtocolLyricsSources>(sources => sources.OnlySourceId == "lyricsplus" && !sources.CacheOnly)))
            .ReturnsAsync([new ProtocolLyricsCandidate("lyricsplus", Lyrics("line"))]);
        resolver.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(),
            It.Is<ProtocolLyricsSources>(sources => sources.CacheOnly && sources.ReadOnly)))
            .ReturnsAsync([new ProtocolLyricsCandidate("amll", Lyrics("word"))]);
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lyricsplus", "amll"]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(Policy());
        var service = new LyricsRefreshService(store, factory, resolver.Object, providers.Object, policies.Object,
            new ManualTimeProvider(clock.UtcNow), new(), NullLogger<LyricsRefreshService>.Instance);
        Assert.Equal(new LyricsRefreshResult(1, 1, 1), await service.RefreshAsync(new("lyricsplus"), "enabled", default));
        Assert.Equal("amll", (await store.CurrentAsync(owner, recording, default))!.Lyrics!.Source);
        resolver.VerifyAll();
    }

    [Fact]
    public async Task SourceImprovabilityIsAppliedBeforeTheRunCap()
    {
        var unknownDuration = Song;
        unknownDuration.Duration = null;
        await store.ObserveAsync(Context, unknownDuration, Lyrics("line"), null);
        var other = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new() { Id = other, CreatedByUserId = owner, Title = "Other" });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                CanonicalRecordingId = recording,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                OccurrenceKey = "frequent",
                State = ListeningEventState.Completed,
                ListenedAt = clock.UtcNow,
                TrackReference = recording.ToString("D")
            });
            await db.SaveChangesAsync();
        }
        var song = Song;
        song.CanonicalRecordingId = other;
        await store.ObserveAsync(Context, song, Lyrics("plain"), 120_000);
        Assert.Equal(other, (await store.DueAsync("spotify", null, 1, default)).Single().CanonicalRecordingId);
    }

    [Fact]
    public async Task AnAccountRevisionChangeDuringLookupPreventsPublication()
    {
        await store.ObserveAsync(Context, Song, Lyrics("line"), 120_000);
        var state = (await store.DueAsync(null, null, 25, default)).Single();
        var account = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "lyricsplus", Enabled = true, Revision = 2 });
            await db.SaveChangesAsync();
        }
        var candidate = Lyrics("word");
        candidate.CacheProvenance = $"{account:N}:1";
        Assert.False(await store.CompleteAsync(state, candidate, "recovered", default));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("old", (await verify.RecordingLyricsRefreshes.SingleAsync()).SourceId);
        Assert.False(await LyricsRefreshSidecars.CanPublishAsync(verify, candidate.CacheProvenance, default));
        candidate.CacheProvenance = $"{account:N}:2";
        Assert.True(await store.CompleteAsync(state, candidate, "recovered", default));
        Assert.False(await LyricsRefreshSidecars.CanPublishAsync(verify, candidate.CacheProvenance, default));
        var row = await verify.ProviderAccounts.SingleAsync();
        row.OwnerUserId = null;
        row.Revision = 3;
        await verify.SaveChangesAsync();
        Assert.False(await LyricsRefreshSidecars.CanPublishAsync(verify, candidate.CacheProvenance, default));
        candidate.CacheProvenance = $"{account:N}:3";
        Assert.True(await LyricsRefreshSidecars.CanPublishAsync(verify, candidate.CacheProvenance, default));
    }

    private static EffectiveProviderPolicySnapshot Policy() => new(
        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["lyricsplus"]),
        ImmutableHashSet<string>.Empty, "lossless", 0);

    internal static LyricsInfo Lyrics(string format, int end = 120) => new()
    {
        Source = format == "word" ? "new" : "old",
        Duration = 120,
        PlainLyrics = format == "plain" ? "First words" : null,
        SyncedLyrics = format == "plain" ? null : "[00:01.000]First words",
        TimedLines = format == "plain" ? null : [new("First words", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(end - 1),
            format == "word" ? [new("First ", TimeSpan.FromSeconds(1)), new("words", TimeSpan.FromSeconds(2))] : null)]
    };

    public async Task DisposeAsync() => await database.DisposeAsync();
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-06T03:00:00Z"); }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
