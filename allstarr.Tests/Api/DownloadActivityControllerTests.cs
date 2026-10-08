using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class DownloadActivityControllerTests
{
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
        deliveries.MarkDelivered(tenantId, userId, "ext-deezer-song-123", "device-1");
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
        var controller = CreateController([source], [resolver], deliveries);
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
        var controller = CreateController([source],
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
    [Trait("Category", "Sqlite")]
    public async Task NowPlaying_QueriesPortableTimestampColumnsWithConvertedParameters()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
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
        var controller = CreateController([source], [], contextFactory: factory);
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
        var tenant = Guid.CreateVersion7();
        var viewerId = Guid.CreateVersion7();
        var viewer = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "admin",
            new AllstarrPrincipal(tenant, viewerId, "jellyfin", "backend", "admin", "Admin", true),
            "artwork", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var permissions = new Mock<IBackendLibraryAccessResolver>();
        permissions.Setup(item => item.ResolveUserAsync(viewerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(viewer, new(true, ["music"])));
        var controller = CreateController([], [resolver], libraryAccess: permissions.Object);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = AdministratorSession(tenant, viewerId);

        var result = await controller.GetPlaybackArtwork("local-item", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal([1, 2, 3], file.FileContents);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        permissions.Setup(item => item.ResolveUserAsync(viewerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(viewer, BackendLibraryAccess.Unavailable));
        Assert.IsType<NotFoundResult>(await controller.GetPlaybackArtwork("local-item", default));
        controller.HttpContext.Items.Remove(AdminAuthSessionService.HttpContextSessionItemKey);
        Assert.IsType<NotFoundResult>(await controller.GetPlaybackArtwork("local-item", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NowPlaying_SameDeviceAndTrack_SeparatesListenersAndDelivery(bool administrator)
    {
        var tenant = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        const string track = "ext-deezer-song-123";
        var now = DateTime.UtcNow;
        var source = new StubPlaybackSource(
            new("same-device", track, 0, now, owner, "owner", "Owner", TenantId: tenant),
            new("same-device", track, 0, now.AddSeconds(1), other, "other", "Other", TenantId: tenant));
        using var deliveries = new PlaybackDeliveryActivityStore();
        deliveries.MarkDelivered(tenant, other, track, "same-device");
        var controller = CreateController([source], [], deliveries);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "owner",
            UserName = "Owner",
            IsAdministrator = administrator,
            TenantId = tenant,
            AllstarrUserId = owner,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = now.AddHours(1)
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.GetNowPlaying(default)).Value));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(administrator ? 2 : 1, items.Length);
        var own = Assert.Single(items, item => item.GetProperty("UserId").GetGuid() == owner);
        Assert.False(own.GetProperty("Scrobbled").GetBoolean());
        Assert.Equal(administrator ? "/api/admin/ui/users/owner/avatar" : "/api/admin/auth/me/avatar",
            own.GetProperty("AvatarUrl").GetString());
        if (administrator)
            Assert.True(Assert.Single(items, item => item.GetProperty("UserId").GetGuid() == other)
                .GetProperty("Scrobbled").GetBoolean());
        controller.HttpContext.Items.Clear();
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetNowPlaying(default)).StatusCode);
        Assert.IsType<NotFoundResult>(await controller.GetPlaybackArtwork(track, default));
    }

    private static DownloadActivityController CreateController(
        IEnumerable<IPlaybackActivitySource> playbackSources,
        IEnumerable<IPlaybackMetadataResolver> metadataResolvers,
        IPlaybackDeliveryActivitySource? playbackDeliveries = null,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null,
        IBackendLibraryAccessResolver? libraryAccess = null)
    {
        if (libraryAccess == null)
        {
            var permissions = new Mock<IBackendLibraryAccessResolver>();
            permissions.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BackendLibraryAccessContext(null, BackendLibraryAccess.Unavailable));
            libraryAccess = permissions.Object;
        }
        var controller = new DownloadActivityController(
            playbackSources,
            metadataResolvers,
            new MediaAssetResolver(
                new TestMemoryApplicationCache(),
                NullLogger<MediaAssetResolver>.Instance),
            NullLogger<DownloadActivityController>.Instance,
            libraryAccess,
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

    private static AdminAuthSession AdministratorSession(Guid tenantId, Guid? userId = null) => new()
    {
        SessionId = "session",
        UserId = "admin",
        UserName = "Admin",
        IsAdministrator = true,
        TenantId = tenantId,
        AllstarrUserId = userId ?? Guid.CreateVersion7(),
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
