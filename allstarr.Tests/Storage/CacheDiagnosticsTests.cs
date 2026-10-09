using allstarr.Controllers;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class CacheDiagnosticsTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private readonly string _mediaPath = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-cache-diagnostics-media-{Guid.CreateVersion7():N}");
    private HybridApplicationCache _cache = null!;
    private MemoryApplicationCache _hot = null!;
    private FileMediaApplicationCache _media = null!;
    private TestClock _clock = null!;
    private readonly ApplicationCacheActivityMetrics _activity = new();

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        var factory = new TestFactory(_database.Options);
        _clock = new TestClock(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        _hot = new MemoryApplicationCache(_clock);
        _media = new FileMediaApplicationCache(
            new FileMediaCacheOptions(_mediaPath),
            _clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        _cache = new HybridApplicationCache(
            _hot,
            _media,
            activityMetrics: _activity, contextFactory: factory, clock: _clock);

    }

    [Fact]
    public async Task CategoryPolicySuppliesDefaultExpiry()
    {
        const string key = "playlist:discovery:v3:shared:00000000000000000000000000000000:1:fixture:digest";
        Assert.True(await _cache.SetStringAsync(key, "{}"));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(4);
        Assert.Equal("{}", await _cache.GetStringAsync(key));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        Assert.Null(await _cache.GetStringAsync(key));
    }

    [Fact]
    public async Task Snapshot_ReportsEveryTierAndScopedPurgesStayIsolated()
    {
        Assert.True(await _cache.SetStringAsync("odesli:translate:v2:track-1:spotify", "metadata"));
        Assert.True(await _cache.SetStringAsync("artwork:payload:v1:track-1", "media"));

        var snapshot = await _cache.GetDiagnosticsAsync();
        Assert.Equal(1, snapshot.Memory.EntryCount);
        Assert.Equal(1, snapshot.Media.EntryCount);
        Assert.Equal(8, snapshot.Memory.PayloadBytes);
        Assert.Equal(5, snapshot.Media.PayloadBytes);
        Assert.Equal(1, snapshot.Memory.Writes);
        Assert.Equal(1, snapshot.Media.Writes);
        _activity.RecordCoalesced();
        _activity.RecordStaleServe();
        _activity.RecordUpstreamBytesAvoided(128);
        snapshot = await _cache.GetDiagnosticsAsync();
        Assert.Equal(1, snapshot.Activity.CoalescedRequests);
        Assert.Equal(1, snapshot.Activity.StaleServes);
        Assert.Equal(128, snapshot.Activity.UpstreamBytesAvoided);
        Assert.Equal(16 * 1024 * 1024, snapshot.ArtworkLimits.MaximumEntryBytes);
        Assert.Equal(16_000_000, snapshot.ArtworkLimits.MaximumDecodedPixels);
        var metadataCategory = Assert.Single(
            snapshot.Categories,
            item => item.Category == ApplicationCacheCategory.ProviderResponse.ToString());
        Assert.True(metadataCategory.Enabled);
        Assert.Equal(1, metadataCategory.EntryCount);
        Assert.Equal(8, metadataCategory.PayloadBytes);
        var artworkCategory = Assert.Single(
            snapshot.Categories,
            item => item.Category == ApplicationCacheCategory.Artwork.ToString());
        Assert.True(artworkCategory.Enabled);
        Assert.Equal(1, artworkCategory.EntryCount);
        Assert.Equal(5, artworkCategory.PayloadBytes);

        Assert.Equal(1, await _cache.PurgeMediaAsync());
        Assert.Equal("metadata", await _cache.GetStringAsync("odesli:translate:v2:track-1:spotify"));
        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:track-1"));
        snapshot = await _cache.GetDiagnosticsAsync();
        Assert.Equal(1, snapshot.Memory.Hits);
        Assert.Equal(1, snapshot.Media.Misses);

        Assert.True(await _cache.SetStringAsync("artwork:payload:v1:track-2", "media"));
        Assert.Equal(1, await _cache.PurgeMetadataAsync());
        Assert.Null(await _cache.GetStringAsync("odesli:translate:v2:track-1:spotify"));
        Assert.Equal("media", await _cache.GetStringAsync("artwork:payload:v1:track-2"));

        Assert.Equal(1, await _cache.PurgeAllAsync());
        Assert.Null(await _cache.GetStringAsync("artwork:payload:v1:track-2"));
    }

    [Fact]
    public async Task Controller_RequiresAdministratorAndRejectsArbitraryScopes()
    {
        var controller = new CacheDiagnosticsController(_cache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        Assert.IsType<UnauthorizedObjectResult>(await controller.Get());

        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] =
            Session(isAdministrator: false);
        var forbidden = Assert.IsType<ObjectResult>(await controller.Get());
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] =
            Session(isAdministrator: true);
        var ok = Assert.IsType<OkObjectResult>(await controller.Get());
        Assert.Equal(
            0,
            Assert.IsType<ApplicationCacheDiagnosticsSnapshot>(ok.Value)
                .ExtensionStorage.ActiveExtensions);
        Assert.IsType<BadRequestObjectResult>(await controller.Purge("arbitrary:*"));

        Assert.True(await _cache.SetStringAsync("lyrics:v2:fixture", "lyrics"));
        Assert.True(await _cache.SetStringAsync("odesli:translate:v2:fixture:spotify", "provider"));
        Assert.IsType<OkObjectResult>(await controller.PurgeCategory("lyrics"));
        Assert.Null(await _cache.GetStringAsync("lyrics:v2:fixture"));
        Assert.Equal("provider", await _cache.GetStringAsync("odesli:translate:v2:fixture:spotify"));
        Assert.IsType<BadRequestObjectResult>(await controller.PurgeCategory("not-a-category"));
        Assert.IsType<BadRequestObjectResult>(await controller.PurgeCategory("0"));
    }

    [Fact]
    public async Task MaintenancePreviewAndRunCoverMetadataAndMediaTiers()
    {
        Assert.True(await _cache.SetStringAsync(
            "search:v2:expired",
            "metadata",
            TimeSpan.FromMinutes(1)));
        Assert.True(await _cache.SetStringAsync(
            "artwork:payload:v1:expired",
            "media",
            TimeSpan.FromMinutes(1)));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        var preview = await _cache.PreviewMaintenanceAsync();
        Assert.Equal(1, preview.Metadata.ExpiredEntries);
        Assert.Equal(1, preview.Media.ExpiredEntries);

        Assert.Equal(2, await _cache.CleanupAsync());
        Assert.Equal(0, (await _cache.PreviewMaintenanceAsync()).Metadata.ExpiredEntries);
        Assert.Equal(0, (await _cache.PreviewMaintenanceAsync()).Media.ExpiredEntries);
    }

    [Fact]
    public async Task MaintenanceRemovesOnlyUnreferencedAgedArtworkPayloads()
    {
        var referencedKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(new string('a', 64));
        var orphanedKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(new string('b', 64));
        var descriptorKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "playlist", "playlist-1", "revision-1"));
        Assert.True(await _cache.SetStringAsync(referencedKey, "referenced"));
        Assert.True(await _cache.SetStringAsync(orphanedKey, "orphaned"));
        Assert.True(await _cache.SetStringAsync(
            descriptorKey,
            JsonSerializer.Serialize(new { PayloadKey = referencedKey })));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(6);

        var preview = await _cache.PreviewMaintenanceAsync();
        Assert.Equal(1, preview.UnreferencedArtworkPayloads);
        Assert.False(preview.ArtworkReferenceScanLimitReached);

        var maintenance = new ApplicationCacheMaintenanceService(
            _cache,
            NullLogger<ApplicationCacheMaintenanceService>.Instance);
        Assert.Equal(1, await maintenance.RunOnceAsync());
        Assert.Equal("referenced", await _cache.GetStringAsync(referencedKey));
        Assert.Null(await _cache.GetStringAsync(orphanedKey));
    }

    [Fact]
    public async Task MaintenanceRemovesStaleProviderAccountScopes()
    {
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        await using (var database = new AllstarrDbContext(_database.Options))
        {
            database.AddRange(
                new UserRecord
                {
                    Id = userId,
                    DisplayName = "Cache scope",
                    Enabled = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "fixture",
                    BackendPrincipalId = userId.ToString("N"),
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                },
                new ProviderAccountRecord
                {
                    Id = accountId,
                    OwnerUserId = userId,
                    ProviderId = "spotify",
                    DisplayName = "Cache scope",
                    Enabled = true,
                    Revision = 2,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            await database.SaveChangesAsync();
        }

        var current = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            userId, accountId, 2, "spotify", null, null, 100);
        var stale = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            userId, accountId, 1, "spotify", null, null, 100);
        Assert.True(await _cache.SetStringAsync(current, "current"));
        Assert.True(await _cache.SetStringAsync(stale, "stale"));

        Assert.Equal(1, (await _cache.PreviewMaintenanceAsync()).Metadata.StaleAuthorizationScopeEntries);
        Assert.Equal(1, await _cache.CleanupAsync());
        Assert.Equal("current", await _cache.GetStringAsync(current));
        Assert.Null(await _cache.GetStringAsync(stale));
    }

    [Fact]
    public async Task MaintenanceRemovesOlderArtworkRevisionsDeterministically()
    {
        var first = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "playlist", "playlist-1", "revision-1", 96, 96));
        var second = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "playlist", "playlist-1", "revision-2", 96, 96));
        const string descriptor = """{"PayloadKey":"artwork:payload:v1:fixture"}""";
        Assert.True(await _cache.SetStringAsync(first, descriptor, TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await _cache.SetStringAsync(second, descriptor, TimeSpan.FromHours(1)));

        Assert.Equal(1, (await _cache.PreviewMaintenanceAsync()).Metadata.SupersededEntries);
        Assert.Equal(1, await _cache.CleanupAsync());
        Assert.Null(await _cache.GetStringAsync(first));
        Assert.Equal(descriptor, await _cache.GetStringAsync(second));
    }

    [Fact]
    public async Task Restart_PreservesLyricsAndReferencedArtworkWhileMetadataStartsCold()
    {
        var payload = CacheKeyBuilder.BuildMediaAssetPayloadKey(new string('c', 64));
        var descriptor = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "album", "album-1", "revision-1"));
        await _cache.SetStringAsync("search:v2:restart", "metadata");
        await _cache.SetStringAsync("lyrics:v2:restart", "lyrics");
        await _cache.SetStringAsync(payload, "artwork");
        await _cache.SetStringAsync(descriptor, JsonSerializer.Serialize(new { PayloadKey = payload }));
        using var memory = new MemoryApplicationCache(_clock);
        using var files = new FileMediaApplicationCache(new FileMediaCacheOptions(_mediaPath), _clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        var restarted = new HybridApplicationCache(memory, files, clock: _clock);
        _clock.UtcNow += TimeSpan.FromMinutes(6);

        Assert.Null(await restarted.GetStringAsync("search:v2:restart"));
        Assert.Equal(0, await restarted.CleanupAsync());
        Assert.Equal("lyrics", await restarted.GetStringAsync("lyrics:v2:restart"));
        Assert.Equal("artwork", await restarted.GetStringAsync(payload));
        Assert.NotNull(await restarted.GetStringAsync(descriptor));
        Assert.Equal(3, (await restarted.GetDiagnosticsAsync()).Media.EntryCount);
    }

    [Fact]
    public async Task IncompleteArtworkReferenceScan_ReportsNoUnsafeReclamation()
    {
        var payload = CacheKeyBuilder.BuildMediaAssetPayloadKey(new string('d', 64));
        var good = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "album", "good", "revision-1"));
        var bad = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, "jellyfin", "album", "bad", "revision-1"));
        await _cache.SetStringAsync(payload, "artwork");
        await _cache.SetStringAsync(good, JsonSerializer.Serialize(new { PayloadKey = payload }));
        await _cache.SetStringAsync(bad, "{broken");
        _clock.UtcNow += TimeSpan.FromMinutes(6);

        var preview = await _cache.PreviewMaintenanceAsync();
        Assert.True(preview.ArtworkReferenceScanLimitReached);
        Assert.Equal(0, preview.UnreferencedArtworkPayloads);
        Assert.Equal(1, preview.Metadata.UnknownOwnerEntries);
        Assert.Equal(1, await _cache.CleanupAsync());
        Assert.Equal("artwork", await _cache.GetStringAsync(payload));
        Assert.Null(await _cache.GetStringAsync(bad));
    }

    [Fact]
    public async Task PatternPurge_ReachesLyricsWithoutClearingMetadataOrOtherFiles()
    {
        await _cache.SetStringAsync("search:v2:keep", "metadata");
        await _cache.SetStringAsync("lyrics:v2:a%_\\x", "lyrics");
        await _cache.SetStringAsync("lyrics:v2:other", "keep");
        Assert.Equal(1, await _cache.DeleteByPatternAsync("lyrics:v2:a%_\\?"));
        Assert.Equal("metadata", await _cache.GetStringAsync("search:v2:keep"));
        Assert.Equal("keep", await _cache.GetStringAsync("lyrics:v2:other"));
    }

    public async Task DisposeAsync()
    {
        _hot.Dispose();
        _media.Dispose();

        if (Directory.Exists(_mediaPath))
        {
            Directory.Delete(_mediaPath, recursive: true);
        }
        if (_database is not null) await _database.DisposeAsync();
    }

    private static AdminAuthSession Session(bool isAdministrator) => new()
    {
        SessionId = "session",
        UserId = "backend-user",
        UserName = "tester",
        IsAdministrator = isAdministrator,
        JellyfinAccessToken = "token",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
