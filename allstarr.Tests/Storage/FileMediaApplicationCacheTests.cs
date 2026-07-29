using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

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
        await _cache.SetStringAsync("artwork:payload:v1:expired", "value", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:expired"));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Quota_RemovesOldestEntry()
    {
        await _cache.SetStringAsync("artwork:payload:v1:first", new string('a', 40));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        await _cache.SetStringAsync("artwork:payload:v1:second", new string('b', 40));

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

    [Fact]
    public async Task Cleanup_RemovesMalformedOrphanedAndTemporaryFiles()
    {
        await _cache.SetStringAsync("artwork:payload:v1:malformed", "value");
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

    [Fact]
    public async Task Cleanup_RemovesEntriesWithoutTtl()
    {
        const string key = "artwork:payload:v1:no-ttl";
        await _cache.SetStringAsync(key, "value");
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
