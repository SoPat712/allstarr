using allstarr.Core.Operations;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class MemoryApplicationCacheTests
{
    [Fact]
    public async Task OverwriteAndRestart_KeepOneEntryThenStartCold()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        Assert.True(await cache.SetStringAsync("search:v2:one", "first"));
        Assert.True(await cache.SetStringAsync("search:v2:one", "second"));
        Assert.Equal("second", await cache.GetStringAsync("search:v2:one"));
        Assert.Equal(1, cache.GetUsageSnapshot().EntryCount);
        Assert.Equal(6, cache.GetUsageSnapshot().PayloadBytes);
        using var restarted = new MemoryApplicationCache(new TestClock());
        Assert.Null(await restarted.GetStringAsync("search:v2:one"));
    }

    [Fact]
    public async Task ConcurrentWrites_RemainAtomicAndBounded()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        using var start = new Barrier(8);
        var writes = Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            return cache.SetStringAsync("search:v2:concurrent", $"value-{index}").GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Assert.All(await Task.WhenAll(writes), Assert.True);
        Assert.StartsWith("value-", await cache.GetStringAsync("search:v2:concurrent"));
        Assert.Equal(1, cache.GetUsageSnapshot().EntryCount);
        Assert.Equal(7, cache.GetUsageSnapshot().PayloadBytes);
    }

    [Fact]
    public async Task AbsoluteExpiry_RespectsCallerLifetimeAndDoesNotSlideOnReads()
    {
        var clock = new TestClock();
        using var cache = new MemoryApplicationCache(clock);
        Assert.True(await cache.SetStringAsync("search:v2:expiry", "value", TimeSpan.FromMinutes(10)));
        clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Equal("value", await cache.GetStringAsync("search:v2:expiry"));
        clock.UtcNow += TimeSpan.FromMinutes(4);
        Assert.Null(await cache.GetStringAsync("search:v2:expiry"));
        Assert.False(await cache.ExistsAsync("search:v2:expiry"));
        Assert.Equal(0, cache.GetUsageSnapshot().EntryCount);
    }

    [Fact]
    public async Task PatternDelete_HandlesWildcardsAndLiteralPunctuationWithoutClearingOtherEntries()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        await cache.SetStringAsync("search:v2:a%_\\x", "one");
        await cache.SetStringAsync("search:v2:aZZx", "other");
        await cache.SetStringAsync("search:v2:playlist-one", "playlist");
        Assert.Equal(1, await cache.DeleteByPatternAsync("search:v2:a%_\\?"));
        Assert.Equal(1, await cache.DeleteByPatternAsync("search:v2:playlist-*"));
        Assert.Equal("other", await cache.GetStringAsync("search:v2:aZZx"));
        Assert.True(await cache.DeleteAsync("search:v2:aZZx"));
        Assert.False(await cache.DeleteAsync("search:v2:aZZx"));
    }

    [Theory]
    [InlineData("artwork:payload:v1:fixture")]
    [InlineData("lyrics:v2:fixture")]
    [InlineData("unknown:key")]
    [InlineData("")]
    public async Task IneligibleKeys_AreColdMisses(string key)
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        Assert.False(await cache.SetStringAsync(key, "value"));
        Assert.Null(await cache.GetStringAsync(key));
    }

    [Fact]
    public async Task EntrySizeLimit_AcceptsFormerDatabasePayloadsAndRejectsOversizedReplacement()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        var payload = new string('x', MemoryApplicationCache.MaximumEntryBytes);
        Assert.True(await cache.SetStringAsync("search:v2:large", payload));
        Assert.False(await cache.SetStringAsync("search:v2:large", payload + "x"));
        Assert.Equal(payload, await cache.GetStringAsync("search:v2:large"));
    }

    [Fact]
    public async Task GlobalByteBudget_EvictsLeastRecentlyUsedAcrossCategories()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        var payload = new string('x', MemoryApplicationCache.MaximumEntryBytes);
        for (var i = 0; i < 16; i++) await cache.SetStringAsync($"search:v2:{i}", payload);
        await cache.GetStringAsync("search:v2:0");
        await cache.SetStringAsync("playback:metadata:v1:new", payload);
        Assert.Null(await cache.GetStringAsync("search:v2:1"));
        Assert.Equal(payload, await cache.GetStringAsync("search:v2:0"));
        Assert.Equal(16, cache.GetUsageSnapshot().EntryCount);
        Assert.Equal(MemoryApplicationCache.MaximumBytes, cache.GetUsageSnapshot().PayloadBytes);
    }

    [Fact]
    public async Task GlobalCountBudget_BoundsEmptyValues()
    {
        using var cache = new MemoryApplicationCache(new TestClock());
        for (var i = 0; i <= MemoryApplicationCache.MaximumEntries; i++)
            await cache.SetStringAsync($"playback:metadata:v1:{i}", "");
        Assert.Equal(MemoryApplicationCache.MaximumEntries, cache.GetUsageSnapshot().EntryCount);
        Assert.Null(await cache.GetStringAsync("playback:metadata:v1:0"));
    }

    [Fact]
    public async Task CategoryBudgetsAndDisable_LeaveOtherCategoriesAlone()
    {
        var settings = new CacheSettings();
        settings.CategoryMaximumEntries[nameof(ApplicationCacheCategory.SearchResults)] = 2;
        settings.CategoryMaximumMegabytes[nameof(ApplicationCacheCategory.ProviderResponse)] = 1;
        using var cache = new MemoryApplicationCache(new TestClock(), Options.Create(settings));
        await cache.SetStringAsync("search:v2:first", "first");
        await cache.SetStringAsync("search:v2:second", "second");
        await cache.GetStringAsync("search:v2:first");
        await cache.SetStringAsync("search:v2:third", "third");
        Assert.Null(await cache.GetStringAsync("search:v2:second"));
        var payload = new string('x', 600 * 1024);
        await cache.SetStringAsync("odesli:translate:v2:first:spotify", payload);
        await cache.SetStringAsync("odesli:translate:v2:second:spotify", payload);
        Assert.Null(await cache.GetStringAsync("odesli:translate:v2:first:spotify"));
        Assert.Equal(payload, await cache.GetStringAsync("odesli:translate:v2:second:spotify"));
        Assert.Equal(2, await cache.DeleteCategoryAsync(ApplicationCacheCategory.SearchResults));
        Assert.Equal(payload, await cache.GetStringAsync("odesli:translate:v2:second:spotify"));
        settings.CategoryEnabled[nameof(ApplicationCacheCategory.ProviderResponse)] = false;
        Assert.Null(await cache.GetStringAsync("odesli:translate:v2:second:spotify"));
        Assert.False(await cache.SetStringAsync("odesli:translate:v2:disabled:spotify", "blocked"));
    }

    private sealed class TestClock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
