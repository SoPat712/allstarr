using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

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
    [InlineData("image:jellyfin:primary:../track")]
    [InlineData("playlist:image:release/radar")]
    [InlineData("artwork:spotify:album")]
    public async Task MediaKey_RoundTripsThroughHashedPaths(string key)
    {
        Assert.True(await _cache.SetStringAsync(key, "\"image-bytes\"", TimeSpan.FromHours(1)));
        Assert.Equal("\"image-bytes\"", await _cache.GetStringAsync(key));

        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.All(files, path => Assert.DoesNotContain("..", Path.GetFileName(path), StringComparison.Ordinal));
        Assert.All(files, path => Assert.DoesNotContain("release", path, StringComparison.OrdinalIgnoreCase));
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
        await _cache.SetStringAsync("image:expired", "value", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Null(await _cache.GetStringAsync("image:expired"));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Quota_RemovesOldestEntry()
    {
        await _cache.SetStringAsync("image:first", new string('a', 40));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        await _cache.SetStringAsync("image:second", new string('b', 40));

        Assert.Null(await _cache.GetStringAsync("image:first"));
        Assert.Equal(new string('b', 40), await _cache.GetStringAsync("image:second"));
    }

    [Fact]
    public async Task PatternDelete_RemovesOnlyMatchingMediaKeys()
    {
        await _cache.SetStringAsync("image:one", "one");
        await _cache.SetStringAsync("playlist:image:two", "two");

        Assert.Equal(1, await _cache.DeleteByPatternAsync("playlist:image:*"));
        Assert.Equal("one", await _cache.GetStringAsync("image:one"));
        Assert.Null(await _cache.GetStringAsync("playlist:image:two"));
    }

    [Fact]
    public async Task Cleanup_RemovesMalformedOrphanedAndTemporaryFiles()
    {
        await _cache.SetStringAsync("image:malformed", "value");
        var entryFiles = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var metadataPath = Assert.Single(
            entryFiles,
            path => path.EndsWith(".json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(metadataPath, "{not-json");

        var shard = Path.Combine(_root, "ff");
        Directory.CreateDirectory(shard);
        await File.WriteAllTextAsync(Path.Combine(shard, "orphan.payload"), "orphan");
        await File.WriteAllTextAsync(
            Path.Combine(shard, "interrupted.payload.1.tmp"),
            "temporary");

        Assert.Equal(3, await _cache.CleanupAsync());
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
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
