using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReportCaptureReadsNormalSearchCacheWithoutRoutingOrProviderCalls(bool managed, bool playing)
    {
        await using var fixture = await ReportCacheFixture.CreateAsync(managed);
        var context = await fixture.CaptureAsync(playing);
        Assert.Equal("Fresh song", context.Song!.Title);
        Assert.Equal("Fresh artist", context.Song.Artist);
        Assert.Equal("Fresh album", context.Song.Album);
        Assert.Equal(120000, context.Song.DurationMilliseconds);
        Assert.Equal("deezer", context.Song.CatalogProviderId);
        Assert.Null(context.Song.ExternalSnapshotId);
        Assert.Equal("timeout", Assert.Single(context.Checks).ReasonCode);
        if (playing)
        {
            Assert.Equal("Feishin", context.Playback!.ClientName);
            Assert.False(context.Playback.SourceConfirmed);
            Assert.Null(context.Playback.ProviderId);
        }
        else Assert.Null(context.Playback);
        fixture.NoOutboundCalls();
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("principal")]
    [InlineData("instance")]
    public async Task ReportCacheFactsNeverCrossExactActorBackendScope(string changed)
    {
        await using var fixture = await ReportCacheFixture.CreateAsync(false);
        var current = fixture.Session;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync();
            if (changed == "user")
            {
                current = fixture.SessionFor(Guid.NewGuid(), "other-principal", "backend");
                db.Users.Add(new()
                {
                    Id = current.AllstarrUserId!.Value,
                    Enabled = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "other-principal",
                    DisplayName = "Other"
                });
            }
            if (changed == "principal")
            {
                user.BackendPrincipalId = "other-principal";
                current = fixture.SessionFor(user.Id, user.BackendPrincipalId, "backend");
            }
            if (changed == "instance")
            {
                user.BackendInstanceId = "other-instance";
                current = fixture.SessionFor(user.Id, "principal", user.BackendInstanceId);
            }
            await db.SaveChangesAsync();
        }
        var context = await fixture.Service.ContextAsync(current, new("ext-deezer-song-track"), default);
        Assert.Null(context.Song!.Title);
        Assert.Null(context.Song.Artist);
        Assert.Empty(context.Checks);
        fixture.NoOutboundCalls();
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("other-account")]
    [InlineData("removed")]
    public async Task ReportCacheFactsRequireCurrentAuthorizedAccountAndRevision(string changed)
    {
        await using var fixture = await ReportCacheFixture.CreateAsync(true);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            if (changed == "revision") account.Revision++;
            else
            {
                if (changed == "removed") account.SettingsJson = "{\"__removed\":true}";
                else
                {
                    var other = Guid.NewGuid();
                    db.Users.Add(new()
                    {
                        Id = other,
                        Enabled = true,
                        BackendType = "jellyfin",
                        BackendInstanceId = "backend",
                        BackendPrincipalId = "other",
                        DisplayName = "Other"
                    });
                    account.OwnerUserId = other;
                }
                db.ProviderAccounts.Add(new()
                {
                    Id = Guid.NewGuid(),
                    ProviderId = "deezer",
                    OwnerUserId = fixture.Session.AllstarrUserId,
                    Enabled = true,
                    DisplayName = "Current account",
                    Revision = 1
                });
            }
            await db.SaveChangesAsync();
        }
        var context = await fixture.CaptureAsync(false);
        Assert.Null(context.Song!.Title);
        Assert.Empty(context.Checks);
        fixture.NoOutboundCalls();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("external-id")]
    [InlineData("local")]
    [InlineData("miss")]
    public async Task ReportCacheRejectsMismatchedLocalOrMissingFactsWithoutFetching(string changed)
    {
        await using var fixture = await ReportCacheFixture.CreateAsync(false);
        if (changed == "miss") await fixture.Cache.PurgeAllAsync();
        else
        {
            var song = new Song { Id = "ext-deezer-song-track", Title = "Wrong cached title", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "track" };
            if (changed == "provider") song.ExternalProvider = "qobuz";
            if (changed == "external-id") song.ExternalId = "other-track";
            if (changed == "local") song.IsLocal = true;
            await fixture.Cache.SetStringAsync(fixture.SongKey, JsonSerializer.Serialize(song), TimeSpan.FromMinutes(5));
        }
        var context = await fixture.CaptureAsync(false);
        Assert.Null(context.Song!.Title);
        Assert.Empty(context.Checks);
        fixture.NoOutboundCalls();
    }

    private sealed class ReportCacheFixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase database;
        private readonly Mock<IProviderRouter> router = new(MockBehavior.Strict);
        private readonly Mock<IProviderMetadataCapability> metadata = new(MockBehavior.Strict);
        private readonly Mock<IHttpClientFactory> http = new(MockBehavior.Strict);
        private readonly ReportCacheActivity activity = new();
        public ReportCacheFactory Factory { get; }
        public TestMemoryApplicationCache Cache { get; } = new();
        public string SongKey { get; private set; } = "";
        public AdminAuthSession Session { get; }
        public ProblemReportService Service { get; private set; } = null!;

        private ReportCacheFixture(SqliteTestDatabase database, ProtocolExecutionContext context)
        {
            this.database = database;
            Factory = new(database.Options);
            Session = SessionFor(context.Principal!.UserId, context.VerifiedBackendPrincipalId, context.BackendInstanceId);
        }
        public AdminAuthSession SessionFor(Guid id, string principal, string instance) => new()
        {
            SessionId = "fixture",
            AllstarrUserId = id,
            UserId = principal,
            UserName = "User",
            IsAdministrator = false,
            BackendType = "Jellyfin",
            BackendInstanceId = instance,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        public static async Task<ReportCacheFixture> CreateAsync(bool managed)
        {
            var context = Context();
            var fixture = new ReportCacheFixture(await SqliteTestDatabase.CreateAsync(), context);
            var account = managed ? new ProviderAccountContext(Guid.NewGuid(), "deezer", ProviderAccountScope.Personal, 1,
                ownerUserId: context.Principal!.UserId) : null;
            await using (var db = fixture.Factory.CreateDbContext())
            {
                db.Users.Add(new()
                {
                    Id = context.Principal!.UserId,
                    Enabled = true,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "principal",
                    DisplayName = "User"
                });
                if (account != null) db.ProviderAccounts.Add(new()
                {
                    Id = account.AccountId,
                    OwnerUserId = account.OwnerUserId,
                    ProviderId = "deezer",
                    DisplayName = "Fixture account",
                    Enabled = true,
                    Revision = 1
                });
                await db.SaveChangesAsync();
            }
            fixture.metadata.SetupGet(item => item.ProviderId).Returns("deezer");
            fixture.metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
            fixture.metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                    [new(new("deezer", ProviderResourceKind.Track, "track"), "Fresh song", [new("Fresh artist")], albumTitle: "Fresh album", duration: TimeSpan.FromSeconds(120))])));
            var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor("deezer", "Deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, managed ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None,
                    "1", ["searchTracks", "getTrack"], managed ? [ProviderAccountScope.Personal, ProviderAccountScope.Shared] : []), new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease"])],
                new ProviderPermissionDescriptor()), [fixture.metadata.Object, Mock.Of<IProviderStreamingCapability>(item => item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Streaming)])]);
            fixture.router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, registry.GetRequired("deezer"), registry.GetRequired("deezer").Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Metadata),
                        fixture.metadata.Object, new(request.Actor, "deezer", account, request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                    new(request.CorrelationId, ProviderCapabilityKind.Metadata, "deezer", account?.AccountId, [])));
            fixture.router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, registry.GetRequiredCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming)));
            fixture.router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => EmptyPlan<IProviderDownloadCapability>(request));
            var songKeys = new ConcurrentBag<string>();
            var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
            cache.SetupGet(item => item.IsEnabled).Returns(true);
            cache.Setup(item => item.GetStringAsync(It.IsAny<string>())).Returns((string key) => fixture.Cache.GetStringAsync(key));
            cache.Setup(item => item.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
                .Returns(async (string key, string value, TimeSpan? expiry) =>
                {
                    using var json = JsonDocument.Parse(value);
                    if (json.RootElement.TryGetProperty("ExternalId", out var external) && external.GetString() == "track") songKeys.Add(key);
                    return await fixture.Cache.SetStringAsync(key, value, expiry);
                });
            var gateway = new ProtocolProviderGateway(fixture.router.Object, registry, fixture.http.Object, applicationCache: cache.Object);
            Assert.Single((await gateway.SearchAsync(context, "Fresh", 10, 0, 0, "deezer")).Songs);
            fixture.SongKey = Assert.Single(songKeys.Distinct());
            fixture.router.Reset();
            fixture.metadata.Reset();
            var health = new ProviderRuntimeHealth();
            var key = account == null ? ProviderRuntimeStatusKey.CreateAccountFree("deezer", "metadata") :
                ProviderRuntimeStatusKey.CreateManaged("deezer", "metadata", account.AccountId);
            health.Record(key, account?.Revision, false, 1, "timeout");
            fixture.Service = new(fixture.Factory, new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>(MockBehavior.Strict),
                [fixture.activity], health: health, registry: registry, providerGateway: gateway);
            return fixture;
        }

        public Task<ProblemReportContext> CaptureAsync(bool playing)
        {
            if (playing) activity.States = [new("device", "ext-deezer-song-track", 1, DateTime.UtcNow, Session.AllstarrUserId,
                Client: "Feishin")];
            return Service.ContextAsync(Session, new("ext-deezer-song-track", Playback: playing ? new(Session.AllstarrUserId!.Value, "device") : null), default);
        }
        public void NoOutboundCalls()
        {
            router.VerifyNoOtherCalls();
            metadata.VerifyNoOtherCalls();
            http.VerifyNoOtherCalls();
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    private sealed class ReportCacheActivity : IPlaybackActivitySource
    {
        public PlaybackActivityState[] States { get; set; } = [];
        public IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge) => States;
    }
    private sealed class ReportCacheFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); }
}
