using System.Text.Json;
using System.Reflection;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class DiskMetadataCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"allstarr-metadata-{Guid.NewGuid():N}");
    private readonly Clock clock = new();
    private readonly List<IDisposable> stores = [];
    private readonly Guid listener = Guid.NewGuid();
    private const string Body = "{\"Items\":[{\"Id\":\"second\"},{\"Id\":\"first\"}],\"TotalRecordCount\":2}";

    [Theory]
    [InlineData("recommendation", 360)]
    [InlineData("Album:detail", 10080)]
    [InlineData("Track:detail", 10080)]
    [InlineData("Artist:detail", 10080)]
    [InlineData("search", 10)]
    public async Task RestartRetainsExactPayloadAndOriginalExpiry(string kind, int minutes)
    {
        var key = Key(listener, kind);
        var first = Create();
        Assert.True(await first.Cache.SetStringAsync(key, Body, TimeSpan.FromMinutes(minutes)));
        await first.Disk.FlushWritesAsync();
        clock.UtcNow = clock.UtcNow.AddMinutes(minutes - 1);
        var restarted = Create();
        Assert.Equal(Body, await restarted.Cache.GetStringAsync(key));
        Assert.Equal(Body, await restarted.Memory.GetStringAsync(key));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.Null(await restarted.Cache.GetStringAsync(key));
        Assert.Null(await Create().Cache.GetStringAsync(key));
    }

    [Fact]
    public async Task MemoryHitsAndWarmUpDoNotWaitForBusyDisk()
    {
        var first = Create();
        var key = Key(listener, "Album");
        await first.Cache.SetStringAsync(key, Body);
        await first.Disk.FlushWritesAsync();
        var restarted = Create();
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first.Disk)!;
        await gate.WaitAsync();
        try
        {
            Assert.Equal(Body, await first.Cache.GetStringAsync(key).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Null(await first.Cache.GetStringAsync(Key(listener, "Track")).WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { gate.Release(); }
        gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restarted.Disk)!;
        await gate.WaitAsync();
        try { await restarted.Cache.WarmRecentAsync(new HashSet<Guid> { listener }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { gate.Release(); }
        Assert.Null(await restarted.Memory.GetStringAsync(key));
    }

    [Fact]
    public async Task SimilarAlbumsReaderRestoresItsExistingScopedAnswer()
    {
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(Array.Empty<string>());
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "main", "listener",
            new AllstarrPrincipal(listener, "jellyfin", "main", "listener", "Listener", false), "test", clock.UtcNow.AddMinutes(5), default);
        var scope = SimilarAlbumsReader.CacheKey(listener, "main", "album", 12, null, []);
        var key = CacheKeyBuilder.BuildProtocolMetadataKey("allstarr", scope, "similar-albums", "album");
        var expected = new ClientSimilarAlbums("album", "fixture",
            [new("second", "Second", "Artist", null, 2026, 2, null, "fixture", true, null),
             new("first", "First", "Artist", null, 2025, 3, null, "library", false, null)]);
        var first = Create();
        await first.Cache.SetAsync(key, expected, TimeSpan.FromHours(6));
        await first.Disk.FlushWritesAsync();
        var reader = new SimilarAlbumsReader(providers.Object, null!, null!, null!, null!, applicationCache: Create().Cache);
        var restored = await reader.ReadAsync(new DefaultHttpContext(), execution, "album", 12);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(restored));
        providers.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RestartPreservesListenerAndAccountRevisionScopes()
    {
        var key = Key(listener, "Album:detail");
        var first = Create();
        await first.Cache.SetStringAsync(key, Body);
        await first.Disk.FlushWritesAsync();
        var restarted = Create();
        Assert.Null(await restarted.Cache.GetStringAsync(Key(Guid.NewGuid(), "Album:detail")));
        var account = Guid.NewGuid();
        string Search(long revision) => CacheKeyBuilder.BuildProviderSearchKey("fixture", account, revision, listener.ToString(), "song", 10, true, false, false);
        await restarted.Cache.SetStringAsync(Search(1), Body, TimeSpan.FromMinutes(10));
        await restarted.Disk.FlushWritesAsync();
        var next = Create();
        Assert.Null(await next.Cache.GetStringAsync(Search(2)));
        Assert.Equal(Body, await next.Cache.GetStringAsync(Search(1)));
        Assert.Equal(Body, await next.Cache.GetStringAsync(key));
    }

    [Fact]
    public async Task MetadataSharesDiskQuotaAndCleanupWithMedia()
    {
        var first = Create(maximumBytes: 64);
        var key = Key(listener, "Album");
        await first.Cache.SetStringAsync(key, new string('a', 40), TimeSpan.FromMinutes(1));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await first.Disk.SetStringAsync("artwork:payload:v1:fixture", new string('b', 40));
        await first.Disk.FlushWritesAsync();
        Assert.Null(await Create(maximumBytes: 64).Cache.GetStringAsync(key));
        Assert.True((await first.Disk.GetUsageAsync()).PayloadBytes <= 64);
        await first.Cache.SetStringAsync(key, "value", TimeSpan.FromMinutes(1));
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await first.Cache.CleanupAsync();
        Assert.Null(await Create().Cache.GetStringAsync(key));
    }

    [Theory]
    [InlineData("*.payload")]
    [InlineData("*.json")]
    public async Task CorruptionIsIgnoredAndRewritten(string pattern)
    {
        var key = Key(listener, "Album");
        var first = Create();
        await first.Cache.SetStringAsync(key, Body);
        await first.Disk.FlushWritesAsync();
        var file = Assert.Single(Directory.GetFiles(root, pattern, SearchOption.AllDirectories));
        await File.WriteAllTextAsync(file, "broken");
        Assert.Equal(Body, await first.Cache.GetStringAsync(key));
        var restarted = Create();
        Assert.Null(await restarted.Cache.GetStringAsync(key));
        Assert.True(await restarted.Cache.SetStringAsync(key, Body));
        await restarted.Disk.FlushWritesAsync();
        Assert.Equal(Body, await Create().Cache.GetStringAsync(key));
    }

    [Fact]
    public async Task DeleteAndMetadataPurgeCannotResurrectDiskEntries()
    {
        var first = Create();
        var key = Key(listener, "Album");
        await first.Cache.SetStringAsync(key, Body);
        Assert.True(await first.Cache.DeleteAsync(key));
        await first.Disk.FlushWritesAsync();
        Assert.Null(await Create().Cache.GetStringAsync(key));
        await first.Cache.SetStringAsync(key, Body);
        await first.Cache.PurgeCategoryAsync(ApplicationCacheCategory.ProviderResponse);
        Assert.Null(await Create().Cache.GetStringAsync(key));
        await first.Cache.SetStringAsync(key, Body);
        await first.Cache.PurgeMediaAsync();
        await first.Disk.FlushWritesAsync();
        Assert.Equal(Body, await Create().Cache.GetStringAsync(key));
        await first.Cache.PurgeMetadataAsync();
        Assert.Null(await Create().Cache.GetStringAsync(key));
    }

    [Fact]
    public async Task WarmUpReadsOnlyRecentEntriesForSelectedListenerWithoutExtendingExpiry()
    {
        var first = Create();
        var key = Key(listener, "Album");
        await first.Cache.SetStringAsync(key, Body, TimeSpan.FromMinutes(1));
        await first.Disk.FlushWritesAsync();
        var other = Create(owner: Guid.NewGuid());
        await other.Cache.SetStringAsync(Key(Guid.NewGuid(), "Album"), Body);
        await other.Disk.FlushWritesAsync();
        var restarted = Create();
        await restarted.Cache.WarmRecentAsync(new HashSet<Guid> { listener }, CancellationToken.None);
        Assert.Equal(Body, await restarted.Memory.GetStringAsync(key));
        Assert.Equal(1, restarted.Memory.GetUsageSnapshot().EntryCount);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.Null(await restarted.Cache.GetStringAsync(key));
    }

    [Fact]
    public async Task CredentialsAreKeptOutOfDiskPayloads()
    {
        var first = Create();
        var key = Key(listener, "Album");
        await first.Cache.SetStringAsync(key, Body);
        foreach (var value in new[] { "{\"Token\":\"fixture\"}", "{\"CoverArtUrl\":\"https://example.test/image?api_key=fixture\"}",
            JsonSerializer.Serialize(new { Body = "{\"Token\":\"fixture\"}" }) })
        {
            Assert.True(await first.Cache.SetStringAsync(key, value));
            Assert.Equal(value, await first.Memory.GetStringAsync(key));
            await first.Disk.FlushWritesAsync();
            Assert.Null(await Create().Cache.GetStringAsync(key));
        }
    }

    [Theory]
    [InlineData("feed:read:v1:fixture")]
    [InlineData("playlist:discovery:v3:fixture")]
    [InlineData("playback:signal:dedupe:v2:fixture")]
    public async Task OtherMetadataRemainsMemoryOnly(string key)
    {
        var first = Create();
        await first.Cache.SetStringAsync(key, Body);
        Assert.Equal(Body, await first.Cache.GetStringAsync(key));
        Assert.Null(await Create().Cache.GetStringAsync(key));
    }

    private (HybridApplicationCache Cache, MemoryApplicationCache Memory, FileMediaApplicationCache Disk) Create(long maximumBytes = 65536, Guid? owner = null)
    {
        var memory = new MemoryApplicationCache(clock);
        var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(Path.Combine(root, "media"), MaximumBytes: maximumBytes, MaximumEntryBytes: 8192,
            MetadataRootPath: Path.Combine(root, "metadata")), clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        stores.Add(memory);
        stores.Add(disk);
        var http = new DefaultHttpContext();
        var user = owner ?? listener;
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "main", "listener",
            new AllstarrPrincipal(user, "jellyfin", "main", "listener", "Listener", false), "test", clock.UtcNow.AddMinutes(5), default);
        return (new HybridApplicationCache(memory, disk, clock: clock, httpContextAccessor: new HttpContextAccessor { HttpContext = http }), memory, disk);
    }

    private static string Key(Guid owner, string kind) => kind == "search"
        ? CacheKeyBuilder.BuildProviderSearchKey("fixture", null, null, owner.ToString(), "song", 10, true, false, false)
        : CacheKeyBuilder.BuildProtocolMetadataKey("fixture", owner.ToString(), kind, "1");

    public void Dispose()
    {
        foreach (var store in stores) store.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    }
}
