using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class FavoriteDownloadRoutingTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _jobId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.Zero));
        await using var db = await _factory.CreateDbContextAsync();
        db.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "route-decisions",
            Name = "Route decisions",
            CreatedAt = _clock.UtcNow
        });
        db.Users.Add(new PlatformUserRecord
        {
            Id = _userId,
            TenantId = _tenantId,
            DisplayName = "Route user",
            Status = PlatformUserStatus.Active,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        db.Jobs.Add(new DurableJobRecord
        {
            Id = _jobId,
            ScopeKey = $"user:{_tenantId:N}:{_userId:N}",
            TenantId = _tenantId,
            OwnerUserId = _userId,
            ProviderCapability = "Download",
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('a', 64),
            CorrelationId = "route-correlation",
            Type = "favorite.action",
            PayloadJson = "{}",
            IdempotencyKey = "favorite-download-job",
            State = DurableJobState.Running,
            MaxAttempts = 5,
            MaxDeferrals = 5,
            AvailableAt = _clock.UtcNow,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            Revision = 1
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task FavoriteDownload_LogsUnavailableRouteWithoutPersistingDecisions()
    {
        var favoriteEvent = new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            LibraryScopeId = "music",
            ItemId = "ext-deezer-song-track-1",
            JobId = _jobId,
            CorrelationId = "favorite-route-empty"
        };
        var action = new FavoriteActionRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            EventId = favoriteEvent.Id,
            ActionType = "download",
            IdempotencyKey = "empty-route",
            AttemptCount = 1
        };
        var request = new ProviderRouteRequest(
            ProviderCapabilityKind.Download,
            new ProviderActorContext(
                _tenantId,
                ProviderActorKind.SystemJob,
                null,
                durableJobId: _jobId,
                actingForUserId: _userId),
            Policy(),
            "favorite-download",
            favoriteEvent.CorrelationId,
            _clock.UtcNow.AddMinutes(30),
            ["deezer"],
            [new ProviderRouteProviderState("deezer", availableQualities: Enum.GetValues<ProviderAudioQuality>())],
            new ProviderLibraryContext(_tenantId, "music"),
            new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "track-1"),
            action.IdempotencyKey);
        var plan = new ProviderRoutePlan<IProviderDownloadCapability>(
            request,
            [],
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                request.Capability,
                null,
                null,
                [new ProviderRouteCandidateDecision(
                    "deezer", null, ProviderRouteDecisionStatus.Rejected, "account-required", 0)]));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync(plan);
        var providers = new Mock<IProviderRegistry>(MockBehavior.Strict);
        providers.Setup(item => item.FindByCapability(ProviderCapabilityKind.Download, true))
            .Returns([Descriptor("deezer")]);
        var output = new StringWriter();
        using var loggerProvider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information"
            }).Build(), output, TextWriter.Null);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var managedDownloads = new ManagedTrackDownloadService(
            router.Object,
            providers.Object,
            null!,
            _clock,
            loggerFactory.CreateLogger<ManagedTrackDownloadService>());
        var executor = new FavoriteDownloadActionExecutor(managedDownloads, _factory);

        var result = await executor.ExecuteAsync(favoriteEvent, action, default);
        action.AttemptCount = 2;
        var retried = await executor.ExecuteAsync(favoriteEvent, action, default);

        Assert.False(result.Succeeded);
        Assert.False(retried.Succeeded);
        Assert.Equal("favorite_download_route_unavailable", result.ErrorCode);
        Assert.Equal("favorite_download_route_unavailable", retried.ErrorCode);
        var log = output.ToString();
        var entries = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("fields"))
            .ToArray();
        Assert.All(entries, entry => Assert.Equal("Download", entry.GetProperty("Capability").GetString()));
        var stopped = entries.Where(entry => entry.GetProperty("Outcome").GetString() == "Stopped").ToArray();
        Assert.Equal(2, stopped.Length);
        Assert.All(stopped, entry => Assert.Equal("no-authorized-candidate", entry.GetProperty("ReasonCode").GetString()));
        Assert.DoesNotContain(_userId.ToString(), log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_jobId.ToString(), log, StringComparison.OrdinalIgnoreCase);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Null(context.Model.FindEntityType("allstarr.Core.Routing.ProviderRouteDecisionEntity"));
        Assert.Null(context.Model.FindEntityType("allstarr.Core.Routing.ProviderRouteOutcomeEntity"));
        router.VerifyAll();
        providers.VerifyAll();
    }

    private static ProviderExecutionPolicy Policy() => new(
        new ProviderQualityPolicy(
            ProviderAudioQuality.Any,
            ProviderAudioQuality.HighResolution,
            allowTranscode: false),
        ProviderExplicitContentPolicy.Allow,
        allowFallback: true,
        allowSharedAccount: false,
        allowManagedDownloads: true);

    private static ProviderDescriptor Descriptor(string providerId) => new(
        providerId,
        providerId,
        "Route decision test provider",
        ProviderOrigin.BuiltIn,
        "1",
        "1.0",
        [new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Download,
            ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None,
            "1.0",
            ["checkAvailability", "download"])],
        new ProviderPermissionDescriptor());

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
