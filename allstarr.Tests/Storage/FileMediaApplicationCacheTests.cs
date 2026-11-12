using allstarr.Core.Caching;
using allstarr.Core.Operations;
using allstarr.Core.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using System.Reflection;

namespace allstarr.Tests;

public sealed class FileMediaApplicationCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-media-cache-{Guid.CreateVersion7():N}");
    private TestClock _clock = null!;
    private FileMediaApplicationCache _cache = null!;

    public Task InitializeAsync()
    {
        _clock = new TestClock(new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.Zero));
        _cache = new FileMediaApplicationCache(
            new FileMediaCacheOptions(_root, MaximumBytes: 64, MaximumEntryBytes: 48),
            _clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("artwork:payload:v1:../track")]
    [InlineData("artwork:payload:v1:release/radar")]
    [InlineData("artwork:payload:v1:album")]
    public async Task MediaKey_RoundTripsThroughHashedPaths(string key)
    {
        Assert.True(await _cache.SetStringAsync(key, "\"image-bytes\"", TimeSpan.FromHours(1)));
        Assert.Equal("\"image-bytes\"", await _cache.GetStringAsync(key));

        await _cache.FlushWritesAsync();

        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.All(files, path => Assert.DoesNotContain("..", Path.GetFileName(path), StringComparison.Ordinal));
        Assert.All(files, path => Assert.DoesNotContain("release", path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("lyrics:v3:lyricsplus:fixture")]
    [InlineData("lyrics:v3:musixmatch:fixture")]
    [InlineData("lyrics:v4:musixmatch:fixture")]
    [InlineData("lrclib:automatic:v2:lyrics:v2:fixture")]
    public void NewLyricsNamespacesKeepTheMediaTier(string key)
    {
        Assert.True(ApplicationCachePolicyRegistry.TryClassify(key, out var category));
        Assert.Equal(ApplicationCacheCategory.Lyrics, category);
        Assert.Equal(ApplicationCacheStorageTier.Media, ApplicationCachePolicyRegistry.Resolve(key).StorageTier);
    }

    [Fact]
    public async Task CachedWordTimingSurvivesAFileCacheRestart()
    {
        var options = new FileMediaCacheOptions(Path.Combine(_root, "timing"), MaximumBytes: 16_384, MaximumEntryBytes: 8192);
        var first = new FileMediaApplicationCache(options, _clock, NullLogger<FileMediaApplicationCache>.Instance);
        var data = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "fixture",
            ProviderLyricsFormat.LineTimed, "[00:01.235]Words", timedLines:
            [new("Words", TimeSpan.FromSeconds(1), segments: [new("Words", TimeSpan.FromTicks(12_345_678), null, true, false)])]);
        const string key = "lyrics:v3:musixmatch:fixture";
        Assert.True(await first.SetAsync(key, data, TimeSpan.FromHours(1)));
        await first.FlushWritesAsync();
        var restarted = new FileMediaApplicationCache(options, _clock, NullLogger<FileMediaApplicationCache>.Instance);
        var retained = await restarted.GetAsync<ProviderLyricsResult>(key);
        Assert.NotNull(retained);
        var segment = Assert.Single(Assert.Single(retained.TimedLines!).Segments!);
        Assert.Equal(TimeSpan.FromTicks(12_345_678), segment.Start);
        Assert.True(segment.IsBackground);
        Assert.False(segment.Synthetic);
        Assert.Null(segment.Duration);
        Assert.Equal(data.Content, retained.Content);
    }

    [Fact]
    public async Task QueuedWritesRetainTheirOriginalExpiryWhileDiskIsBusy()
    {
        const string key = "artwork:payload:v1:queued";
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_cache)!;
        await gate.WaitAsync();
        try
        {
            Assert.True(await _cache.SetStringAsync(key, "image", TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("image", await _cache.GetStringAsync(key).WaitAsync(TimeSpan.FromSeconds(1)));
            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            Assert.Null(await _cache.GetStringAsync(key).WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { gate.Release(); }
        await _cache.FlushWritesAsync();
        using var restarted = new FileMediaApplicationCache(new FileMediaCacheOptions(_root), _clock, NullLogger<FileMediaApplicationCache>.Instance);
        Assert.Null(await restarted.GetStringAsync(key));
    }

    [Fact]
    public async Task QueuedHitsPreserveLeastRecentlyUsedOrder()
    {
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_cache)!;
        await gate.WaitAsync();
        try
        {
            await _cache.SetStringAsync("artwork:payload:v1:first", new string('a', 20));
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            await _cache.SetStringAsync("artwork:payload:v1:second", new string('b', 20));
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            Assert.Equal(new string('a', 20), await _cache.GetStringAsync("artwork:payload:v1:first"));
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            await _cache.SetStringAsync("artwork:payload:v1:third", new string('c', 40));
        }
        finally { gate.Release(); }
        await _cache.FlushWritesAsync();
        Assert.Equal(2, (await _cache.GetUsageAsync()).EntryCount);
        Assert.Equal(new string('a', 20), await _cache.GetStringAsync("artwork:payload:v1:first"));
        await _cache.FlushWritesAsync();
        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:second"));
        Assert.Equal(new string('c', 40), await _cache.GetStringAsync("artwork:payload:v1:third"));
    }

    [Fact]
    public async Task MetadataKey_IsRejected()
    {
        Assert.False(await _cache.SetStringAsync("spotify:playlist:items", "metadata"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExpiredEntry_IsRemovedOnRead()
    {
        await _cache.SetStringAsync("artwork:payload:v1:expired", "value", TimeSpan.FromMinutes(1));
        await _cache.FlushWritesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:expired"));
        await _cache.FlushWritesAsync();
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Quota_RemovesOldestEntry()
    {
        await _cache.SetStringAsync("artwork:payload:v1:first", new string('a', 40));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        await _cache.SetStringAsync("artwork:payload:v1:second", new string('b', 40));
        await _cache.FlushWritesAsync();

        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:first"));
        Assert.Equal(new string('b', 40), await _cache.GetStringAsync("artwork:payload:v1:second"));
    }

    [Fact]
    public async Task PatternDelete_RemovesOnlyMatchingMediaKeys()
    {
        await _cache.SetStringAsync("artwork:payload:v1:track-one", "one");
        await _cache.SetStringAsync("artwork:payload:v1:playlist-two", "two");

        Assert.Equal(1, await _cache.DeleteByPatternAsync("artwork:payload:v1:playlist-*"));
        Assert.Equal("one", await _cache.GetStringAsync("artwork:payload:v1:track-one"));
        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:playlist-two"));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{}")]
    [InlineData("{\"key\":null}")]
    public async Task Cleanup_RemovesMalformedOrphanedAndTemporaryFiles(string invalidMetadata)
    {
        await _cache.SetStringAsync("artwork:payload:v1:malformed", "value");
        await _cache.FlushWritesAsync();
        var entryFiles = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var metadataPath = Assert.Single(
            entryFiles,
            path => path.EndsWith(".json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(metadataPath, invalidMetadata);

        var shard = Path.Combine(_root, "ff");
        Directory.CreateDirectory(shard);
        await File.WriteAllTextAsync(Path.Combine(shard, "orphan.payload"), "orphan");
        await File.WriteAllTextAsync(
            Path.Combine(shard, "interrupted.payload.1.tmp"),
            "temporary");

        Assert.Equal(3, await _cache.CleanupAsync());
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cleanup_RemovesEntriesWithoutTtl()
    {
        const string key = "artwork:payload:v1:no-ttl";
        await _cache.SetStringAsync(key, "value");
        await _cache.FlushWritesAsync();
        var metadataPath = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
        metadata["expiresAt"] = null;
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());

        Assert.Equal(1, (await _cache.PreviewCleanupAsync()).NoExpiryEntries);
        Assert.Equal(1, await _cache.CleanupAsync());
        Assert.Null(await _cache.GetStringAsync(key));
    }

    public Task DisposeAsync()
    {
        _cache.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
