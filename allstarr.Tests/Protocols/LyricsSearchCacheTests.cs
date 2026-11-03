using allstarr.Core.Capabilities;
using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class LyricsSearchCacheTests
{
    [Fact]
    public void LyricsSearch_AccountBindingDoesNotChangeProviderResultValueOrJson()
    {
        var result = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "musixmatch", ProviderLyricsFormat.PlainText, "Words");
        var bound = result with { CacheProvenance = "private-scope" };
        Assert.Equal(result, bound);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(bound));
    }

    [Fact]
    public async Task LyricsSearch_CacheWritesNotifyKnownSongsOnlyAfterSuccessfulStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var metadata = new MemoryApplicationCache(clock);
            using var media = new FileMediaApplicationCache(new FileMediaCacheOptions(root, MaximumBytes: 65536, MaximumEntryBytes: 16384),
                clock, NullLogger<FileMediaApplicationCache>.Instance);
            var changes = new LyricsSearchChanges();
            var user = Guid.NewGuid();
            const string key = "lyrics:v3:lrcred:watched";
            changes.WatchCacheKey(key, user, "native");
            var cache = new HybridApplicationCache(metadata, media, writeObserver: changes);
            Assert.True(await cache.SetAsync(key, new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
                "lrcred", ProviderLyricsFormat.PlainText, "Cached line")));
            var changed = Assert.Single(changes.TakeSongs());
            Assert.Equal(user, changed.UserId);
            Assert.Equal("native", changed.ItemId);
            Assert.True(await cache.SetStringAsync(key, "\"Another line\""));
            Assert.Single(changes.TakeSongs());
            Assert.True(await cache.SetStringAsync("lyrics:v3:lrcred:unknown", "\"Unknown song\""));
            Assert.Empty(changes.TakeSongs());
            Assert.False(await cache.SetStringAsync(key, new string('x', 20000)));
            Assert.Empty(changes.TakeSongs());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
