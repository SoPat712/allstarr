using System.Collections.Concurrent;
using allstarr.Core.Caching;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ProtocolLyricsAutomaticTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ASongNoSourceHasLyricsForIsNotAskedAboutAgainForADay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MissCache(clock);
        var first = new Source("lyricsplus", None("lyricsplus"));
        var later = new Source("lrclib", None("lrclib"));
        await using var fixture = new Fixture(cache, clock, first, later) { Recording = Guid.NewGuid() };
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        await cache.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        // Past the six hours one service's own "no lyrics" is held for, the song is still not asked about.
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, later.Calls);
        clock.Advance(TimeSpan.FromHours(18));
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(2, first.Calls);
        Assert.Equal(2, later.Calls);
    }

    [Fact]
    public async Task ARememberedMissHoldsAcrossARestart()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "lyrics-miss-" + Guid.NewGuid().ToString("N"));
        var recording = Guid.NewGuid();
        ProtocolExecutionContext listener;
        try
        {
            using (var before = new MissCache(clock, root))
            {
                var source = new Source("lrclib", None("lrclib"));
                await using var fixture = new Fixture(before, clock, source) { Recording = recording };
                Assert.Null(await fixture.FindAsync(deadline: Minute));
                await before.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
                listener = fixture.Protocol;
            }
            using var after = new MissCache(clock, root);
            var restarted = new Source("lrclib", None("lrclib"));
            await using var again = new Fixture(after, clock, restarted) { Recording = recording, Protocol = listener };
            Assert.Null(await again.FindAsync(deadline: Minute));
            Assert.Equal(0, restarted.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProviderErrorKind.TransientFailure)]
    [InlineData(ProviderErrorKind.RateLimited)]
    public async Task ASourceThatDidNotAnswerKeepsTheMissFromBeingRemembered(ProviderErrorKind failure)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MissCache(clock);
        var answered = new Source("lyricsplus", None("lyricsplus"));
        var down = new Source("lrclib", ProviderOutcome<ProviderLyricsResult>.Failure(new(failure, TimeSpan.FromSeconds(30))));
        await using var fixture = new Fixture(cache, clock, answered, down) { Recording = Guid.NewGuid() };
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        // While the failed source cools down it is not asked, and a skipped source has not answered either.
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(1, down.Calls);
        clock.Advance(TimeSpan.FromMinutes(3));
        down.Immediate = None("lrclib");
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(2, down.Calls);
        await cache.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(2, down.Calls);
    }

    [Fact]
    public async Task ASourceThatTimedOutKeepsTheMissFromBeingRemembered()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MissCache(clock);
        var answered = new Source("lyricsplus", None("lyricsplus"));
        var slow = new Source("lrclib");
        await using var fixture = new Fixture(cache, clock, answered, slow) { Recording = Guid.NewGuid() };
        fixture.LookupOptions.SourceTimeoutMilliseconds = 400;
        var pending = fixture.FindAsync(deadline: Minute);
        await StartAsync(pending, slow);
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(400));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        slow.Completion.TrySetResult(None("lrclib"));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        Assert.Equal(2, slow.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AListenersOwnLookAsksAgainAndClearsTheMissWhenItFindsLyrics(bool chosenSource)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MissCache(clock);
        var source = new Source("lrclib", None("lrclib"));
        await using var fixture = new Fixture(cache, clock, source) { Recording = Guid.NewGuid() };
        Assert.Null(await fixture.FindAsync(deadline: Minute));
        await cache.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(7));
        source.Immediate = Result("lrclib", true);
        var found = await fixture.FindAllAsync(chosenSource ? new ProtocolLyricsSources { OnlySourceId = "lrclib" } : null);
        Assert.Contains(found, candidate => candidate.Lyrics != null);
        Assert.Equal(2, source.Calls);
        Assert.Single(cache.Deleted);
        Assert.Equal("lrclib", (await fixture.FindAsync(deadline: Minute))?.Source);
    }

    private static ProviderOutcome<ProviderLyricsResult> None(string provider) =>
        ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, provider));

    private sealed class MissCache : IApplicationCache, IDisposable
    {
        private readonly FileMediaApplicationCache inner;
        private readonly string root;
        private readonly bool owned;
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Deleted = new();
        internal MissCache(TimeProvider clock, string? path = null)
        {
            owned = path == null;
            root = path ?? Path.Combine(Path.GetTempPath(), "lyrics-miss-" + Guid.NewGuid().ToString("N"));
            inner = new FileMediaApplicationCache(new FileMediaCacheOptions(root), new ClockBridge(clock), NullLogger<FileMediaApplicationCache>.Instance);
        }
        public bool IsEnabled => inner.IsEnabled;
        public Task<string?> GetStringAsync(string key) => inner.GetStringAsync(key);
        public Task<T?> GetAsync<T>(string key) where T : class => inner.GetAsync<T>(key);
        public async Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
        {
            var saved = await inner.SetStringAsync(key, value, expiry);
            if (key.StartsWith("lyrics:miss:v1:", StringComparison.Ordinal)) Written.TrySetResult();
            return saved;
        }
        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class => inner.SetAsync(key, value, expiry);
        public Task<bool> DeleteAsync(string key)
        {
            if (key.StartsWith("lyrics:miss:v1:", StringComparison.Ordinal)) Deleted.Enqueue(key);
            return inner.DeleteAsync(key);
        }
        public Task<bool> ExistsAsync(string key) => inner.ExistsAsync(key);
        public Task<int> DeleteByPatternAsync(string pattern) => inner.DeleteByPatternAsync(pattern);
        public Task<int> PurgeAllAsync() => inner.PurgeAllAsync();
        public void Dispose() { inner.Dispose(); if (owned && Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
