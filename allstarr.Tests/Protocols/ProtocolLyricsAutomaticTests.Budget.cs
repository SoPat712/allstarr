using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed partial class ProtocolLyricsAutomaticTests
{
    [Fact]
    public async Task CachedAutomaticChoiceMatchesTheHighestScoredReadyVersion()
    {
        var versions = new[]
        {
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.WordTimed,
                "[00:01]<00:01>Short <00:02>words") { Metadata = new("short", "Fixture", "Artist", "Album", 10000) },
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed,
                "[01:50]Fitting words") { Metadata = new("fits", "Fixture", "Artist", "Album", 120000) }
        };
        var source = new Source("lrclib") { Cached = ProviderOutcome<ProviderLyricsResult>.Success(versions[0] with { Versions = versions }) };
        await using var fixture = new Fixture(source);
        var song = new Song { Title = "Fixture", Artist = "Artist", Album = "Album", Duration = 120 };
        var ready = await fixture.Resolver.FindAllAsync(fixture.Protocol, song, "recording");
        var best = ready.OrderByDescending(candidate => candidate.Assessment?.Score).First();
        var selected = await fixture.Resolver.FindAsync(fixture.Protocol, song, "recording", new ProtocolLyricsSources());
        Assert.Equal("lrclib:fits", best.SourceId);
        Assert.Equal(best.SourceId, selected?.Source);
        Assert.Contains("duration_fits", best.Assessment!.Reasons);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AutomaticChoiceUsesTheSameTwentyCandidateLimitAsSourceDiscovery()
    {
        var sources = new[] { "lyricsplus", "lrcred", "unison", "paxsenix", "musixmatch" }.Select((provider, index) =>
        {
            var versions = Enumerable.Range(0, 5).Select(version => new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, provider, index == 4 ? ProviderLyricsFormat.WordTimed : ProviderLyricsFormat.PlainText,
                index == 4 ? "[00:01]<00:01>Extra <00:02>words" : "Visible words")
            { Metadata = new(version.ToString(), "Fixture", "Artist", "Album", null) }).ToArray();
            return new Source(provider) { Cached = ProviderOutcome<ProviderLyricsResult>.Success(versions[0] with { Versions = versions }) };
        }).ToArray();
        await using var fixture = new Fixture(sources);
        var song = new Song { Title = "Fixture", Artist = "Artist", Album = "Album" };
        var candidates = await fixture.Resolver.FindAllAsync(fixture.Protocol, song, "recording");
        Assert.Equal(20, candidates.Count);
        var best = candidates.OrderByDescending(candidate => candidate.Assessment?.Score).First();
        var selected = await fixture.Resolver.FindAsync(fixture.Protocol, song, "recording", new ProtocolLyricsSources());
        Assert.Equal(best.SourceId, selected?.Source);
        Assert.Equal("lyricsplus:0", selected?.Source);
    }

    [Fact]
    public async Task UnreadablePinnedBodyFallsBackToTheBestReadyCandidate()
    {
        var unreadable = new Source("lrclib") { Cached = ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed, "Missing timestamps")) };
        var ready = new Source("lyricsplus") { Cached = Result("lyricsplus", false) };
        await using var fixture = new Fixture(unreadable, ready) { Recording = Guid.NewGuid(), Preferred = "lrclib" };
        Assert.Equal("lyricsplus", (await fixture.FindAsync())?.Source);
    }

    [Fact]
    public async Task PermanentSidecarWaitsForTheBestLyricsBeforeSaving()
    {
        var plain = new Source("lrclib", Result("lrclib", false));
        var word = new Source("lyricsplus");
        await using var fixture = new Fixture(plain, word);
        var pending = fixture.FindAsync(new ProtocolLyricsSources(HouseholdOnly: true) { ForPermanentSidecar = true });
        await StartAsync(pending, word);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(pending.IsCompleted);
        word.Completion.SetResult(Result("lyricsplus", true));
        Assert.Equal("lyricsplus", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
    }

    [Fact]
    public async Task SavedProviderChoiceUsesAnAvailableVersionWhenItsBestRecordIsEmpty()
    {
        var version = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.PlainText, "Pinned words")
        { Metadata = new("only", "Fixture", "Artist", "Album", 120000) };
        var pinned = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Available, "lrclib")
        { Versions = new[] { version } }));
        var other = new Source("lyricsplus", Result("lyricsplus", true));
        await using var fixture = new Fixture(other, pinned) { Recording = Guid.NewGuid(), Preferred = "lrclib" };
        var selected = await fixture.FindAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lrclib:only", selected?.Source);
        Assert.Equal("Pinned words", selected?.PlainLyrics);
    }

    [Fact]
    public async Task SavedVersionRemainsIndexedAfterConcurrentLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var index = new allstarr.Core.ClientApi.LyricsSearchIndex(new allstarr.Core.Storage.StorageOptions { DataDirectory = root });
            var versions = new[] { "first", "second" }.Select(key => new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed, "[00:01]" + key)
            { Metadata = new(key, "Fixture", "Artist", "Album", 120000) }).ToArray();
            var source = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Success(versions[0] with { Versions = versions }));
            await using var fixture = new Fixture(index, source)
            { Recording = Guid.NewGuid(), Preferred = "lrclib:second", ExternalProvider = "deezer", ExternalId = "42" };
            var selected = await fixture.FindAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("lrclib:second", selected?.Source);
            await fixture.Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(await index.SnapshotAsync(fixture.Protocol), document => document.Source == "lrclib:second");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SourceTimeoutReleasesCapacityForAFastQueuedProviderWithinTheBudget()
    {
        var slow = new[] { new Source("lyricsplus"), new Source("lrcred"), new Source("unison"), new Source("genius") };
        foreach (var source in slow) source.ObserveCancellation = true;
        var fast = new Source("paxsenix", Result("paxsenix", true));
        await using var fixture = new Fixture(slow.Append(fast).ToArray());
        fixture.LookupOptions.SourceTimeoutMilliseconds = 400;
        var pending = fixture.FindAsync();
        foreach (var source in slow) await StartAsync(pending, source);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal("paxsenix", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal(1, fast.Calls);
        Assert.All(slow, source => Assert.True(Assert.Single(source.Tokens).IsCancellationRequested));
    }

    [Fact]
    public async Task CachedPlainLyricsReturnImmediatelyAndKeepTheSavedChoice()
    {
        var word = new Source("lyricsplus", Result("lyricsplus", true));
        var cached = new Source("lrclib") { Cached = Result("lrclib", false) };
        await using var fixture = new Fixture(word, cached) { Recording = Guid.NewGuid(), Preferred = "lrclib" };
        var result = await fixture.FindAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lrclib", result?.Source);
        Assert.Equal("plain", allstarr.Core.Music.RichLyricsMapper.Map(result)?.Format);
        Assert.Equal(0, cached.Calls);
    }

    [Fact]
    public async Task BestPossibleWordLyricsDoNotWaitForLaterSources()
    {
        var best = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Available, "lyricsplus", ProviderLyricsFormat.WordTimed,
            "[00:01]<00:01>First <00:02>words")));
        var slow = new Source("lrcred");
        await using var fixture = new Fixture(best, slow);
        var result = await fixture.FindAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lyricsplus", result?.Source);
        Assert.Equal("word", allstarr.Core.Music.RichLyricsMapper.Map(result)?.Format);
        Assert.False(slow.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task ResponseBudgetReturnsLineLyricsWhileWordSourceIsStillPending()
    {
        var slow = new Source("lyricsplus");
        var fast = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Success(new(
            ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed, "[00:01]Words")));
        await using var fixture = new Fixture(slow, fast);
        var pending = fixture.FindAsync();
        await StartAsync(pending, fast);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.False(slow.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task KnownLocalLyricsReturnBeforeRemoteUpgradeCompletes()
    {
        var slow = new Source("lyricsplus");
        await using var fixture = new Fixture(slow);
        var pending = fixture.FindAsync(new ProtocolLyricsSources(Backend: _ => Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(
            new() { SyncedLyrics = "[00:01]Local words" })));
        Assert.Equal("backend", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.False(slow.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task SourceDiscoveryReturnsReadyAndUnfinishedSourcesAtTheResponseBudget()
    {
        var slow = new Source("lyricsplus");
        var fast = new Source("lrclib", Result("lrclib", true));
        await using var fixture = new Fixture(slow, fast);
        var pending = fixture.Resolver.FindAllAsync(fixture.Protocol,
            new Song { Title = "Fixture", Artist = "Artist", Album = "Album", Duration = 120 }, "recording");
        await fast.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        var sources = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(Assert.Single(sources, source => source.SourceId == "lrclib").Lyrics);
        Assert.Null(Assert.Single(sources, source => source.SourceId == "lyricsplus").Lyrics);
        Assert.Equal("pending", Assert.Single(sources, source => source.SourceId == "lyricsplus").State);
        Assert.Equal("ready", Assert.Single(sources, source => source.SourceId == "lrclib").State);
        Assert.False(slow.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task LateBetterLyricsPublishAnUpdateAfterTheInitialResponse()
    {
        var slow = new Source("lyricsplus");
        var fast = new Source("lrclib", Result("lrclib", false));
        await using var fixture = new Fixture(slow, fast);
        var pending = fixture.FindAsync();
        await StartAsync(pending, fast);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrclib", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Empty(fixture.Events);
        slow.Completion.SetResult(Result("lyricsplus", true));
        await fixture.EventPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Resolver.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lyrics.updated", Assert.Single(fixture.Events).Type);
        Assert.Equal("ext-deezer-song-fixture", Assert.Single(fixture.Events).Key);
    }

    [Fact]
    public async Task ParallelReadsShareAnUnfinishedLookup()
    {
        var slow = new Source("lyricsplus");
        var fast = new Source("lrclib", Result("lrclib", true));
        await using var fixture = new Fixture(slow, fast);
        var first = fixture.FindAsync();
        await StartAsync(first, fast);
        var second = fixture.FindAsync();
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal("lrclib", (await first.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal("lrclib", (await second.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
        Assert.Equal(1, slow.Calls);
        Assert.Equal(1, fast.Calls);
    }
}
