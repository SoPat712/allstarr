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
    private PostgresTestDatabase _database = null!;
    private readonly string _mediaPath = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-cache-diagnostics-media-{Guid.CreateVersion7():N}");
    private HybridApplicationCache _cache = null!;
    private BoundedHotApplicationCache _hot = null!;
    private FileMediaApplicationCache _media = null!;
    private TestClock _clock = null!;
    private readonly ApplicationCacheActivityMetrics _activity = new();

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        var factory = new TestFactory(_database.Options);
        _clock = new TestClock(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var database = new DatabaseApplicationCache(
            factory,
            _clock,
            NullLogger<DatabaseApplicationCache>.Instance);
        _hot = new BoundedHotApplicationCache(database);
        _media = new FileMediaApplicationCache(
            new FileMediaCacheOptions(_mediaPath),
            _clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        _cache = new HybridApplicationCache(
            _hot,
            _media,
            activityMetrics: _activity);

        await using var context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task CategoryPolicySuppliesDefaultExpiry()
    {
        const string key = "playlist:discovery:v2:global:shared:00000000000000000000000000000000:1:fixture:digest";
        Assert.True(await _cache.SetStringAsync(key, "{}"));

        await using var context = new AllstarrDbContext(_database.Options);
        var entry = await context.Set<ApplicationCacheEntryRecord>().SingleAsync(item => item.Key == key);
        Assert.Equal(ApplicationCacheCategory.PlaylistDiscovery.ToString(), entry.Category);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), entry.ExpiresAt);
    }

    [Fact]
    public async Task Snapshot_ReportsEveryTierAndScopedPurgesStayIsolated()
    {
        Assert.True(await _cache.SetStringAsync("odesli:translate:v2:track-1:spotify", "metadata"));
        Assert.True(await _cache.SetStringAsync("artwork:payload:v1:track-1", "media"));

        var snapshot = await _cache.GetDiagnosticsAsync();
        Assert.Equal(1, snapshot.Database.EntryCount);
        Assert.Equal(1, snapshot.Hot.EntryCount);
        Assert.Equal(1, snapshot.Media.EntryCount);
        Assert.Equal(8, snapshot.Database.PayloadBytes);
        Assert.Equal(8, snapshot.Hot.PayloadBytes);
        Assert.Equal(5, snapshot.Media.PayloadBytes);
        Assert.Equal(1, snapshot.Database.Writes);
        Assert.Equal(1, snapshot.Hot.Writes);
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
        Assert.Equal(1, snapshot.Hot.Hits);
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
            null, null, null, "jellyfin", "playlist", "playlist-1", "revision-1"));
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
