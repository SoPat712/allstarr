using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderRouteDecisionPersistenceTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _jobId = Guid.CreateVersion7();
    private readonly Guid _accountId = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.Zero));
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
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
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _accountId,
            ProviderId = "deezer",
            DisplayName = "Route account",
            Scope = ProviderAccountScope.User,
            TenantId = _tenantId,
            OwnerUserId = _userId,
            Enabled = true,
            Revision = 1,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PlanAndOutcome_AreTenantScopedRedactedAndIdempotent()
    {
        const string rawTrackId = "https://provider.invalid/track?token=do-not-store";
        const string rawRouteKey = "favorite-download:opaque-track-id";
        var request = Request(rawTrackId);
        var decision = new ProviderRouteDecisionRecord(
            request.CorrelationId,
            request.Capability,
            "deezer",
            _accountId,
            [
                new ProviderRouteCandidateDecision(
                    "apple-download",
                    null,
                    ProviderRouteDecisionStatus.Rejected,
                    "account-required",
                    0),
                new ProviderRouteCandidateDecision(
                    "deezer",
                    _accountId,
                    ProviderRouteDecisionStatus.Accepted,
                    "selected",
                    1)
            ]);
        var store = new DurableProviderRouteDecisionStore(_factory, _clock);

        var first = await store.RecordPlanAsync(request, decision, rawRouteKey);
        var repeated = await store.RecordPlanAsync(request, decision, rawRouteKey);
        var outcome = new ProviderRouteExecutionOutcome(
            "attempt:opaque-provider-track-id",
            0,
            "download",
            "deezer",
            _accountId,
            ProviderRouteOutcomeStatus.FallbackAdvanced,
            "fallback-transient-failure",
            "qobuz");
        await store.RecordOutcomeAsync(first, outcome);
        await store.RecordOutcomeAsync(first, outcome);

        Assert.Equal(first, repeated);
        await using var db = await _factory.CreateDbContextAsync();
        var storedPlan = Assert.Single(await db.ProviderRouteDecisions.AsNoTracking().ToListAsync());
        var storedOutcome = Assert.Single(await db.ProviderRouteOutcomes.AsNoTracking().ToListAsync());
        Assert.Equal(_tenantId, storedPlan.TenantId);
        Assert.Equal(_userId, storedPlan.ActorUserId);
        Assert.Equal(_jobId, storedPlan.DurableJobId);
        Assert.Equal("deezer", storedPlan.SelectedProviderId);
        Assert.Equal(_accountId, storedPlan.SelectedProviderAccountId);
        Assert.Equal(ProviderRouteOutcomeStatus.FallbackAdvanced, storedOutcome.Status);
        Assert.Equal("qobuz", storedOutcome.NextProviderId);
        Assert.Matches("^[a-f0-9]{64}$", storedPlan.RouteKey);
        Assert.Matches("^[a-f0-9]{64}$", storedOutcome.OutcomeKey);
        Assert.Equal(2, await db.AuditEvents.CountAsync(item => item.Category == "provider-route"));
        Assert.Equal(2, await db.OutboxMessages.CountAsync(item => item.Type.StartsWith("provider-route.")));

        var durableText = string.Join('\n',
            storedPlan.CandidateDecisionsJson,
            string.Join('\n', await db.AuditEvents.Select(item => item.DetailsJson).ToListAsync()),
            string.Join('\n', await db.OutboxMessages.Select(item => item.PayloadJson).ToListAsync()),
            storedPlan.RouteKey,
            storedOutcome.OutcomeKey);
        Assert.DoesNotContain(rawTrackId, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-store", durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(rawRouteKey, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-provider-track-id", durableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanKey_IsActorScopedAndRejectsAChangedDecisionAlias()
    {
        var request = Request("track-actor-scope");
        var decision = new ProviderRouteDecisionRecord(
            request.CorrelationId,
            request.Capability,
            "deezer",
            _accountId,
            [new("deezer", _accountId, ProviderRouteDecisionStatus.Accepted, "selected", 0)]);
        var store = new DurableProviderRouteDecisionStore(_factory, _clock);
        var first = await store.RecordPlanAsync(request, decision, "shared-caller-key");

        var changed = decision with
        {
            Candidates = [new("deezer", _accountId, ProviderRouteDecisionStatus.Accepted, "selected-after-replan", 0)]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordPlanAsync(request, changed, "shared-caller-key"));

        var otherUserId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new PlatformUserRecord
            {
                Id = otherUserId,
                TenantId = _tenantId,
                DisplayName = "Other route user",
                Status = PlatformUserStatus.Active,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var otherRequest = new ProviderRouteRequest(
            ProviderCapabilityKind.Download,
            new ProviderActorContext(_tenantId, ProviderActorKind.User, otherUserId,
                new ProviderBackendPrincipal("jellyfin", "route-tests", "other-user")),
            Policy(),
            request.OperationId,
            request.CorrelationId,
            request.Deadline,
            ["deezer"],
            library: new ProviderLibraryContext(_tenantId, "music"));
        var otherDecision = new ProviderRouteDecisionRecord(
            otherRequest.CorrelationId,
            otherRequest.Capability,
            "deezer",
            null,
            [new("deezer", null, ProviderRouteDecisionStatus.Accepted, "selected", 0)]);
        var second = await store.RecordPlanAsync(otherRequest, otherDecision, "shared-caller-key");

        Assert.NotEqual(first.Id, second.Id);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.ProviderRouteDecisions.CountAsync());
    }

    [Fact]
    public async Task Outcome_RejectsAHandleFromAnotherTenant()
    {
        var request = Request("track-1");
        var decision = new ProviderRouteDecisionRecord(
            request.CorrelationId,
            request.Capability,
            null,
            null,
            []);
        var store = new DurableProviderRouteDecisionStore(_factory, _clock);
        var handle = await store.RecordPlanAsync(request, decision, "tenant-bound-route");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordOutcomeAsync(
            handle with { TenantId = Guid.CreateVersion7() },
            new ProviderRouteExecutionOutcome(
                "wrong-tenant",
                0,
                "planning",
                null,
                null,
                ProviderRouteOutcomeStatus.Stopped,
                "no-authorized-candidate")));
    }

    [Fact]
    public async Task FavoriteDownload_RecordsAnEmptyPlanBeforeReturningUnavailable()
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
        var store = new Mock<IProviderRouteDecisionStore>(MockBehavior.Strict);
        var handle = new ProviderRouteDecisionHandle(Guid.CreateVersion7(), _tenantId);
        store.Setup(item => item.RecordPlanAsync(
                plan.Request,
                plan.Decision,
                action.IdempotencyKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        var outcomeKeys = new List<string>();
        store.Setup(item => item.RecordOutcomeAsync(
                handle,
                It.Is<ProviderRouteExecutionOutcome>(outcome =>
                    outcome.Stage == "planning" &&
                    outcome.Status == ProviderRouteOutcomeStatus.Stopped &&
                    outcome.ReasonCode == "no-authorized-candidate"),
                It.IsAny<CancellationToken>()))
            .Callback<ProviderRouteDecisionHandle, ProviderRouteExecutionOutcome, CancellationToken>(
                (_, outcome, _) => outcomeKeys.Add(outcome.OutcomeKey))
            .Returns(Task.CompletedTask);
        var executor = new FavoriteDownloadActionExecutor(
            router.Object,
            providers.Object,
            null!,
            _clock,
            _factory,
            store.Object);

        var result = await executor.ExecuteAsync(favoriteEvent, action, default);
        action.AttemptCount = 2;
        var retried = await executor.ExecuteAsync(favoriteEvent, action, default);

        Assert.False(result.Succeeded);
        Assert.False(retried.Succeeded);
        Assert.Equal("favorite_download_route_unavailable", result.ErrorCode);
        Assert.Equal(2, outcomeKeys.Count);
        Assert.NotEqual(outcomeKeys[0], outcomeKeys[1]);
        store.VerifyAll();
    }

    private ProviderRouteRequest Request(string rawTrackId) => new(
        ProviderCapabilityKind.Download,
        new ProviderActorContext(
            _tenantId,
            ProviderActorKind.SystemJob,
            null,
            durableJobId: _jobId,
            actingForUserId: _userId),
        Policy(),
        "favorite-download",
        "route-correlation",
        _clock.UtcNow.AddMinutes(30),
        ["apple-download", "deezer"],
        library: new ProviderLibraryContext(_tenantId, "music"),
        sourceTrackId: new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, rawTrackId),
        idempotencyKey: "favorite-download-route");

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
