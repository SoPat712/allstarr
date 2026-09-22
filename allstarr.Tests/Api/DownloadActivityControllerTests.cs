using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Download;
using allstarr.Services;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DownloadActivityControllerTests
{
    [Fact]
    public async Task Queue_ActivatesWithoutJellyfinServicesForSubsonicMode()
    {
        var controller = CreateController([], [], []);

        var result = await controller.GetDownloadQueue();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(ok.Value).Cast<object>());
    }

    [Fact]
    public async Task Queue_UsesBackendAdapterMetadataForLocalNowPlayingEntry()
    {
        var source = new StubPlaybackSource(
            new PlaybackActivityState("device-1", "local-item", TimeSpan.FromSeconds(12).Ticks, DateTime.UtcNow));
        var resolver = new StubMetadataResolver(
            new PlaybackTrackMetadata(
                "Fixture title",
                "Fixture artist",
                "Fixture album",
                "/api/admin/downloads/artwork/local-item"));
        var controller = CreateController([], [source], [resolver]);

        var result = await controller.GetDownloadQueue();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var document = JsonDocument.Parse(json);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("Fixture title", entry.GetProperty("Title").GetString());
        Assert.Equal("Fixture artist", entry.GetProperty("Artist").GetString());
        Assert.Equal("/api/admin/downloads/artwork/local-item", entry.GetProperty("CoverArtUrl").GetString());
        Assert.True(entry.GetProperty("IsPlaying").GetBoolean());
    }

    [Fact]
    public async Task Queue_ReportsRealPlaybackProgressDurationSourceAndScrobbleState()
    {
        var source = new StubPlaybackSource(
            new PlaybackActivityState("device-1", "local-item", TimeSpan.FromSeconds(30).Ticks, DateTime.UtcNow));
        var resolver = new StubMetadataResolver(
            new PlaybackTrackMetadata("Title", "Artist", "Album", "/art", DurationSeconds: 120));
        var deliveries = new PlaybackDeliveryActivityStore();
        deliveries.MarkDelivered("local-item", "device-1");
        var controller = CreateController([], [source], [resolver], deliveries);

        var result = await controller.GetDownloadQueue();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("jellyfin", entry.GetProperty("ExternalProvider").GetString());
        Assert.Equal(30, entry.GetProperty("PlaybackPositionSeconds").GetInt32());
        Assert.Equal(120, entry.GetProperty("DurationSeconds").GetInt32());
        Assert.Equal(0.25, entry.GetProperty("PlaybackProgress").GetDouble());
        Assert.True(entry.GetProperty("Scrobbled").GetBoolean());
    }

    [Fact]
    public async Task Queue_PreservesArtworkFromAnActiveExternalDownload()
    {
        var download = new DownloadInfo
        {
            SongId = "ext-apple-download-song-123",
            ExternalId = "123",
            ExternalProvider = "apple-download",
            Title = "External title",
            Artist = "External artist",
            CoverArtUrl = "https://artwork.example/cover.jpg",
            DurationSeconds = 180,
            Status = DownloadStatus.Completed,
            StartedAt = DateTime.UtcNow
        };
        var service = new Moq.Mock<IDownloadService>();
        service.Setup(item => item.GetActiveDownloads()).Returns([download]);
        var source = new StubPlaybackSource(
            new PlaybackActivityState("device-1", download.SongId, TimeSpan.FromSeconds(20).Ticks, DateTime.UtcNow));
        var controller = CreateController([service.Object], [source], []);

        var result = await controller.GetDownloadQueue();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(
            "/api/admin/downloads/artwork/ext-apple-download-song-123",
            entry.GetProperty("CoverArtUrl").GetString());
        Assert.True(entry.GetProperty("IsPlaying").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NowPlaying_ProjectsUserClientSourceProgressAndScrobbleState(bool confirmed)
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var source = new StubPlaybackSource(new PlaybackActivityState(
            "device-1",
            "ext-deezer-song-123",
            TimeSpan.FromSeconds(30).Ticks,
            DateTime.UtcNow,
            userId,
            "backend-user-1",
            "Josh",
            "Feishin",
            "Desktop",
            tenantId));
        var resolver = new StubMetadataResolver(
            new PlaybackTrackMetadata("Rocket", "Artist", "Album", "/art", DurationSeconds: 120));
        var deliveries = new PlaybackDeliveryActivityStore();
        deliveries.MarkDelivered("ext-deezer-song-123", "device-1");
        using var streamResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        if (confirmed)
        {
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
                new AllstarrPrincipal(tenantId, userId, "jellyfin", "backend", "principal", "User", false),
                "stream", DateTimeOffset.UtcNow.AddMinutes(1), default, new ProtocolClientDescriptor("client", "device-1"));
            var lease = new ProviderStreamLease("lease", new Uri("https://media.example.test/track"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new ProviderMediaFormat("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry);
            deliveries.StreamOpened(context, "ext-deezer-song-123", ProviderAudioQuality.Any,
                new ProtocolProviderStream(streamResponse, lease, "qobuz", "actual-track"));
        }
        var controller = CreateController([], [source], [resolver], deliveries);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = AdministratorSession(tenantId);

        var result = await controller.GetNowPlaying(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(userId, item.GetProperty("UserId").GetGuid());
        Assert.Equal("Josh", item.GetProperty("UserName").GetString());
        Assert.Equal("Feishin", item.GetProperty("Client").GetString());
        Assert.Equal(confirmed ? "qobuz" : "deezer", item.GetProperty("ProviderId").GetString());
        Assert.Equal("deezer", item.GetProperty("CatalogProviderId").GetString());
        Assert.Equal(confirmed, item.GetProperty("SourceConfirmed").GetBoolean());
        Assert.Equal(confirmed ? "provider-priority" : null,
            item.GetProperty("RouteReason").GetString());
        Assert.Equal(0.25, item.GetProperty("Progress").GetDouble());
        Assert.True(item.GetProperty("Scrobbled").GetBoolean());
        Assert.Equal(60, item.GetProperty("ScrobbleThresholdSeconds").GetDouble());
        Assert.False(item.GetProperty("ScrobbleEligible").GetBoolean());
        Assert.Empty(item.GetProperty("ScrobbleDeliveries").EnumerateArray());
        Assert.Equal("/api/admin/ui/users/backend-user-1/avatar", item.GetProperty("AvatarUrl").GetString());
    }

    [Fact]
    public async Task NowPlaying_IdentifiesNativeLocalPlaybackWithoutAProviderLease()
    {
        var tenantId = Guid.CreateVersion7();
        var source = new StubPlaybackSource(new PlaybackActivityState(
            "device-1", "local-item", 0, DateTime.UtcNow, TenantId: tenantId));
        var controller = CreateController([], [source],
            [new StubMetadataResolver(new PlaybackTrackMetadata("Local", "Artist", null, null))]);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = AdministratorSession(tenantId);

        var result = await controller.GetNowPlaying(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("jellyfin", item.GetProperty("ProviderId").GetString());
        Assert.Equal("native-local-library", item.GetProperty("RouteReason").GetString());
        Assert.True(item.GetProperty("SourceConfirmed").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NowPlaying_QueriesPortableTimestampColumnsWithConvertedParameters()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var factory = new TestDbContextFactory(database.Options);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "now-playing",
                Name = "Now playing",
                CreatedAt = now
            });
            db.Set<PlatformUserRecord>().Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Listener",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = userId,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                LibraryScopeId = "music",
                OccurrenceKey = new string('a', 64),
                State = ListeningEventState.Playing,
                StartedAt = now.AddMinutes(-1),
                UpdatedAt = now,
                SourceKind = "protocol",
                TrackReference = "ext-deezer-song-123",
                ProviderId = "deezer",
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var source = new StubPlaybackSource(new PlaybackActivityState(
            "device-1",
            "ext-deezer-song-123",
            TimeSpan.FromSeconds(30).Ticks,
            now.UtcDateTime,
            userId,
            "backend-user-1",
            "Listener",
            "Client",
            "Device",
            tenantId));
        var controller = CreateController([], [source], [], contextFactory: factory);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = AdministratorSession(tenantId);

        var result = await controller.GetNowPlaying(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("deezer", item.GetProperty("ProviderId").GetString());
    }

    [Fact]
    public async Task Artwork_IsServedThroughProtectedAdminControllerAdapter()
    {
        var resolver = new StubMetadataResolver(
            metadata: null,
            artwork: new PlaybackArtwork([1, 2, 3], "image/jpeg"));
        var controller = CreateController([], [], [resolver]);

        var result = await controller.GetPlaybackArtwork("local-item", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal([1, 2, 3], file.FileContents);
    }

    private static DownloadActivityController CreateController(
        IEnumerable<IDownloadService> downloads,
        IEnumerable<IPlaybackActivitySource> playbackSources,
        IEnumerable<IPlaybackMetadataResolver> metadataResolvers,
        IPlaybackDeliveryActivitySource? playbackDeliveries = null,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null)
    {
        var controller = new DownloadActivityController(
            downloads,
            playbackSources,
            metadataResolvers,
            new MediaAssetResolver(
                new TestMemoryApplicationCache(),
                NullLogger<MediaAssetResolver>.Instance),
            NullLogger<DownloadActivityController>.Instance,
            playbackDeliveries,
            contextFactory)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static AdminAuthSession AdministratorSession(Guid tenantId) => new()
    {
        SessionId = "session",
        UserId = "admin",
        UserName = "Admin",
        IsAdministrator = true,
        TenantId = tenantId,
        JellyfinAccessToken = "token",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
    };

    private sealed class StubPlaybackSource(params PlaybackActivityState[] states)
        : IPlaybackActivitySource
    {
        public IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge) => states;
    }

    private sealed class StubMetadataResolver(
        PlaybackTrackMetadata? metadata,
        PlaybackArtwork? artwork = null) : IPlaybackMetadataResolver
    {
        public Task<PlaybackTrackMetadata?> ResolveAsync(
            string itemId,
            CancellationToken cancellationToken) => Task.FromResult(metadata);

        public Task<PlaybackArtwork?> ResolveArtworkAsync(
            string itemId,
            CancellationToken cancellationToken) => Task.FromResult(artwork);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
