using allstarr.Controllers;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class CacheDiagnosticsTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-cache-diagnostics-{Guid.CreateVersion7():N}.db");
    private readonly string _mediaPath = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-cache-diagnostics-media-{Guid.CreateVersion7():N}");
    private HybridApplicationCache _cache = null!;
    private BoundedHotApplicationCache _hot = null!;
    private FileMediaApplicationCache _media = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        var factory = new TestFactory(options);
        var clock = new TestClock(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var database = new DatabaseApplicationCache(
            factory,
            clock,
            NullLogger<DatabaseApplicationCache>.Instance);
        _hot = new BoundedHotApplicationCache(database);
        _media = new FileMediaApplicationCache(
            new FileMediaCacheOptions(_mediaPath),
            clock,
            NullLogger<FileMediaApplicationCache>.Instance);
        _cache = new HybridApplicationCache(_hot, _media);

        await using var context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task Snapshot_ReportsEveryTierAndScopedPurgesStayIsolated()
    {
        Assert.True(await _cache.SetStringAsync("metadata:track:1", "metadata"));
        Assert.True(await _cache.SetStringAsync("image:track:1", "media"));

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
        Assert.Equal("metadata", await _cache.GetStringAsync("metadata:track:1"));
        Assert.Null(await _cache.GetStringAsync("image:track:1"));
        snapshot = await _cache.GetDiagnosticsAsync();
        Assert.Equal(1, snapshot.Hot.Hits);
        Assert.Equal(1, snapshot.Media.Misses);

        Assert.True(await _cache.SetStringAsync("image:track:2", "media"));
        Assert.Equal(1, await _cache.PurgeMetadataAsync());
        Assert.Null(await _cache.GetStringAsync("metadata:track:1"));
        Assert.Equal("media", await _cache.GetStringAsync("image:track:2"));

        Assert.Equal(1, await _cache.PurgeAllAsync());
        Assert.Null(await _cache.GetStringAsync("image:track:2"));
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
        Assert.IsType<OkObjectResult>(await controller.Get());
        Assert.IsType<BadRequestObjectResult>(await controller.Purge("arbitrary:*"));
    }

    public Task DisposeAsync()
    {
        _hot.Dispose();
        _media.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        if (Directory.Exists(_mediaPath))
        {
            Directory.Delete(_mediaPath, recursive: true);
        }

        return Task.CompletedTask;
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
