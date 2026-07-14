using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using allstarr.Core.Enrichment;

namespace allstarr.Tests;

public sealed class FavoriteActionPipelineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-favorite-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _otherUserId = Guid.CreateVersion7();
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableJobQueue _jobs = null!;
    private FavoriteActionPipeline _pipeline = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "favorites.db")}").Options;
        _factory = new TestFactory(options);
        await using var database = await _factory.CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        database.Tenants.Add(new TenantRecord { Id = _tenantId, Slug = "favorite-tests", Name = "Favorite tests", CreatedAt = now });
        database.Users.AddRange(
            new PlatformUserRecord
            {
                Id = _userId,
                TenantId = _tenantId,
                DisplayName = "Favorite user",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PlatformUserRecord
            {
                Id = _otherUserId,
                TenantId = _tenantId,
                DisplayName = "Other user",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
        await database.SaveChangesAsync();
        _clock = new FakeClock(now);
        _jobs = CreateQueue();
        _pipeline = new FavoriteActionPipeline(_factory, _jobs, _clock);
    }

    [Fact]
    public async Task RepeatedEvent_IsTenantUserScopedAndCreatesOneJobAndAction()
    {
        var request = Request(FavoriteOperation.Favorite, "source-revision-1");
        var first = await _pipeline.RecordAsync(request);
        var repeated = await _pipeline.RecordAsync(request);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.EventId, repeated.EventId);
        Assert.Equal(first.JobId, repeated.JobId);
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Single(await database.Set<FavoriteEventRecord>().ToListAsync());
        Assert.Single(await database.Set<FavoriteActionRecord>().ToListAsync());
        Assert.Single(await database.Jobs.Where(item => item.Type == FavoriteActionPipeline.JobType).ToListAsync());
        Assert.Equal(2, await database.OutboxMessages.CountAsync()); // job.enqueued + favorite.recorded
    }

    [Fact]
    public async Task Restart_RecoversPendingEventAndCompletesVirtualLikedStateOnce()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "restart-revision"));

        // Recreate every process-local service while retaining only the SQLite database.
        var restartedJobs = CreateQueue();
        var restartedPipeline = new FavoriteActionPipeline(_factory, restartedJobs, _clock);
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        var claim = await restartedJobs.ClaimNextAsync("favorite-restart-worker", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(claim);
        var completion = await handler.ExecuteAsync(new DurableJobExecutionContext(claim!, EmptyServices.Instance), default);
        await restartedJobs.CompleteAsync(claim!, completion);

        var status = await restartedPipeline.GetStatusAsync(_tenantId, _userId, receipt.EventId);
        Assert.NotNull(status);
        Assert.Equal(FavoriteEventState.Succeeded, status!.State);
        Assert.Equal(FavoriteActionState.Succeeded, Assert.Single(status.Actions).State);
        await using var database = await _factory.CreateDbContextAsync();
        var favoriteState = Assert.Single(await database.Set<FavoriteStateRecord>().ToListAsync());
        Assert.True(favoriteState.IsFavorite);
        Assert.Equal(receipt.EventId, favoriteState.LastEventId);
    }

    [Fact]
    public async Task SameBackendNotificationForDifferentUser_CreatesDistinctScopedWork()
    {
        var first = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "shared-revision"));
        var other = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "shared-revision", _otherUserId));

        Assert.True(first.Created);
        Assert.True(other.Created);
        Assert.NotEqual(first.EventId, other.EventId);
        Assert.NotEqual(first.JobId, other.JobId);
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await database.Set<FavoriteEventRecord>().CountAsync());
        Assert.Equal(2, await database.Jobs.CountAsync(item => item.Type == FavoriteActionPipeline.JobType));
    }

    [Fact]
    public async Task Unstar_CancelsOnlyPendingFavoriteWorkAndNeverCreatesRemovalAction()
    {
        var favorite = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "state-v1"));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var unstar = await _pipeline.RecordAsync(Request(FavoriteOperation.Unfavorite, "state-v1"));

        await using (var database = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(DurableJobState.Cancelled,
                (await database.Jobs.SingleAsync(item => item.Id == favorite.JobId)).State);
            var oldEvent = await database.Set<FavoriteEventRecord>().SingleAsync(item => item.Id == favorite.EventId);
            Assert.Equal(FavoriteEventState.Cancelled, oldEvent.State);
            var unstarActions = await database.Set<FavoriteActionRecord>()
                .Where(item => item.EventId == unstar.EventId).ToListAsync();
            var action = Assert.Single(unstarActions);
            Assert.Equal(FavoriteActionPipeline.VirtualLikedAction, action.ActionType);
            Assert.DoesNotContain(unstarActions, item => item.ActionType.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                                                       item.ActionType.Contains("remove-file", StringComparison.OrdinalIgnoreCase));
        }

        var claim = await _jobs.ClaimNextAsync("unstar-worker", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(claim);
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        var completion = await handler.ExecuteAsync(new DurableJobExecutionContext(claim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(claim!, completion);
        await using var verified = await _factory.CreateDbContextAsync();
        Assert.False((await verified.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task FavoriteAfterUnstar_StartsNewLifecycleWithoutDuplicatingCurrentNotifications()
    {
        var first = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "protocol-state-v1"));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        await _pipeline.RecordAsync(Request(FavoriteOperation.Unfavorite, "protocol-state-v1"));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var second = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "protocol-state-v1"));
        var repeated = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "protocol-state-v1"));

        Assert.NotEqual(first.EventId, second.EventId);
        Assert.False(repeated.Created);
        Assert.Equal(second.EventId, repeated.EventId);
    }

    [Fact]
    public async Task StatusController_UsesCanonicalOwnerScopeAndDoesNotExposeIdempotencyMaterial()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "private-source-revision"));
        var controller = new FavoriteEventsController(_factory, _pipeline)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "favorite-session",
            UserId = "backend-user",
            UserName = "Favorite user",
            IsAdministrator = false,
            TenantId = _tenantId,
            AllstarrUserId = _userId,
            JellyfinAccessToken = "not-returned",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        var result = Assert.IsType<OkObjectResult>(await controller.Get(receipt.EventId));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("private-source-revision", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EventKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IdempotencyKey", json, StringComparison.Ordinal);
        Assert.Contains(receipt.EventId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestCannotEnableActionDeniedByEffectivePolicy()
    {
        var denied = new FavoriteActionPipeline(_factory, _jobs, _clock,
            new FavoriteActionPolicyOptions { AddToVirtualLiked = true, AutoDownload = false });
        var original = Request(FavoriteOperation.Favorite, "denied-action");
        var request = original with { OptedInActions = ["download"] };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.RecordAsync(request));
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.Set<FavoriteEventRecord>().ToListAsync());
        Assert.Empty(await database.Jobs.Where(item => item.Type == FavoriteActionPipeline.JobType).ToListAsync());
    }

    [Fact]
    public async Task MatchAction_UsesExactOwnerBackendLibraryAndProviderIdentity()
    {
        var identityId = Guid.CreateVersion7();
        var libraryTrackId = Guid.CreateVersion7();
        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.BackendIdentities.Add(new BackendIdentityRecord
            {
                Id = identityId,
                TenantId = _tenantId,
                UserId = _userId,
                BackendType = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                PrincipalId = "backend-user",
                CreatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
            database.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = libraryTrackId,
                TenantId = _tenantId,
                OwnerUserId = _userId,
                BackendIdentityId = identityId,
                LibraryScopeId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                BackendItemId = "local-1",
                FilePath = "/source/never-touched.flac",
                Title = "Fixture",
                Artist = "Fixture",
                DurationMilliseconds = 180000,
                ProviderIdsJson = "{\"fixture\":\"track-1\"}",
                IndexedAt = _clock.UtcNow,
                SourceModifiedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await database.SaveChangesAsync();
        }
        var favoriteEvent = new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            Protocol = "jellyfin",
            BackendInstanceId = "jellyfin-main",
            BackendPrincipalId = "backend-user",
            LibraryScopeId = "music",
            ItemId = "ext-fixture-song-track-1",
            CorrelationId = "match-action-test"
        };
        var action = new FavoriteActionRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            EventId = favoriteEvent.Id,
            ActionType = "match",
            IdempotencyKey = "match-key"
        };

        var result = await new FavoriteMatchActionExecutor(_factory, _clock)
            .ExecuteAsync(favoriteEvent, action, default);

        Assert.True(result.Succeeded);
        await using var verified = await _factory.CreateDbContextAsync();
        var audit = Assert.Single(await verified.AuditEvents.Where(item => item.Category == "favorite-action").ToListAsync());
        Assert.Equal("matched", audit.Outcome);
        Assert.Contains(libraryTrackId.ToString(), audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/source/never-touched.flac", audit.DetailsJson, StringComparison.Ordinal);

        var download = await new FavoriteDownloadActionExecutor(null!, null!, null!, _clock, _factory)
            .ExecuteAsync(favoriteEvent, Action("download"), default);
        var place = await new FavoritePlaceActionExecutor(_factory, null!, null!, null!, new FavoritePlacementOptions())
            .ExecuteAsync(favoriteEvent, Action("place"), default);
        var enrich = await new FavoriteEnrichActionExecutor(_factory, null!, null!, null!, null!)
            .ExecuteAsync(favoriteEvent, Action("enrich"), default);
        Assert.True(download.Succeeded);
        Assert.True(place.Succeeded, $"{place.ErrorCode}: {place.SafeMessage}");
        Assert.True(enrich.Succeeded, $"{enrich.ErrorCode}: {enrich.SafeMessage}");
        FavoriteActionRecord Action(string type) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            EventId = favoriteEvent.Id,
            ActionType = type,
            IdempotencyKey = $"{type}-key"
        };
    }

    [Fact]
    public async Task CompositeActions_RetryFromFailedStageWithoutRepeatingCompletedStages()
    {
        var policy = new FavoriteActionPolicyOptions
        {
            AddToVirtualLiked = false,
            MatchLocalLibrary = true,
            AutoDownload = true,
            PlaceManagedFile = true,
            EnrichMetadata = true,
            RefreshBackendLibrary = true
        };
        var pipeline = new FavoriteActionPipeline(_factory, _jobs, _clock, policy);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "jellyfin-main", "backend-user",
            new AllstarrPrincipal(_tenantId, _userId, "jellyfin", "jellyfin-main", "backend-user", "Favorite user", false),
            "composite-retry", _clock.UtcNow.AddMinutes(5), default, libraryScopeId: "music");
        await pipeline.RecordAsync(new(context, "ext-fixture-song-track-1", FavoriteOperation.Favorite, "chain-v1"));
        var calls = new List<string>();
        var placeAttempts = 0;
        var executors = new IFavoriteActionExecutor[]
        {
            new RecordingExecutor("match", calls), new RecordingExecutor("download", calls),
            new RecordingExecutor("place", calls, () => ++placeAttempts == 1
                ? FavoriteActionExecutionResult.Retry("place-temporary", "Placement will retry.")
                : FavoriteActionExecutionResult.Success()),
            new RecordingExecutor("enrich", calls), new RecordingExecutor("refresh", calls)
        };
        var handler = new FavoriteActionJobHandler(_factory, executors, _clock);
        var first = await _jobs.ClaimNextAsync("favorite-chain-1", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(first);
        var firstCompletion = await handler.ExecuteAsync(new DurableJobExecutionContext(first!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Retry, firstCompletion.Kind);
        await _jobs.CompleteAsync(first!, firstCompletion);
        Assert.Equal(["match", "download", "place"], calls);

        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        var second = await _jobs.ClaimNextAsync("favorite-chain-2", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(second);
        var secondCompletion = await handler.ExecuteAsync(new DurableJobExecutionContext(second!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(second!, secondCompletion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, secondCompletion.Kind);
        Assert.Equal(["match", "download", "place", "place", "enrich", "refresh"], calls);
    }

    [Theory]
    [InlineData("jellyfin", false)]
    [InlineData("subsonic", true)]
    public async Task RefreshAction_SnapshotsOnlyScopedCredentialReferenceIntoChildJob(string protocol, bool needsCredential)
    {
        var credential = needsCredential ? Guid.CreateVersion7() : (Guid?)null;
        var favoriteEvent = new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = _userId,
            JobId = Guid.CreateVersion7(),
            Protocol = protocol,
            BackendInstanceId = $"{protocol}-main",
            BackendPrincipalId = "backend-user",
            LibraryScopeId = "music",
            CorrelationId = "favorite-refresh",
            TargetCredentialReferenceId = credential
        };
        var action = new FavoriteActionRecord
        {
            Id = Guid.CreateVersion7(),
            EventId = favoriteEvent.Id,
            TenantId = _tenantId,
            OwnerUserId = _userId,
            ActionType = "refresh",
            IdempotencyKey = $"refresh-{protocol}"
        };
        var result = await new FavoriteRefreshActionExecutor(new BackendLibraryRefreshOrchestrator(_jobs))
            .ExecuteAsync(favoriteEvent, action, default);

        Assert.True(result.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync(item => item.Type == "library.refresh");
        using var payload = JsonDocument.Parse(job.PayloadJson);
        var value = payload.RootElement.GetProperty("CredentialReferenceId");
        if (needsCredential) Assert.Equal(credential, value.GetGuid()); else Assert.Equal(JsonValueKind.Null, value.ValueKind);
        Assert.DoesNotContain("password", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    private FavoriteMutationRequest Request(FavoriteOperation operation, string revision, Guid? userId = null) => new(
        new ProtocolExecutionContext(ProtocolKind.Jellyfin, "jellyfin-main", "backend-user", new AllstarrPrincipal(
            _tenantId, userId ?? _userId, "jellyfin", "jellyfin-main", "backend-user", "Favorite user", false),
            $"favorite-test-{operation.ToString().ToLowerInvariant()}", _clock.UtcNow.AddMinutes(1), default),
        "external:fixture:track-1", operation, revision);

    private DurableJobQueue CreateQueue()
    {
        var options = new DurableJobOptions
        {
            DefaultMaxAttempts = 3,
            LeaseSeconds = 30,
            PollIntervalMilliseconds = 10,
            MaxPayloadBytes = 64 * 1024
        };
        return new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
    private sealed class RecordingExecutor(string actionType, List<string> calls,
        Func<FavoriteActionExecutionResult>? result = null) : IFavoriteActionExecutor
    {
        public string ActionType => actionType;
        public Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent,
            FavoriteActionRecord action, CancellationToken cancellationToken)
        {
            calls.Add(actionType);
            return Task.FromResult(result?.Invoke() ?? FavoriteActionExecutionResult.Success());
        }
    }
}
