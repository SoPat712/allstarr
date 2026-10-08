using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class FavoriteActionPipelineTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _otherUserId = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableJobQueue _jobs = null!;
    private FavoriteActionPipeline _pipeline = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        await using var database = await _factory.CreateDbContextAsync();
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

    [Theory]
    [InlineData("native-track", 1)]
    [InlineData("ext-fixture-song-track-1", 2)]
    public async Task RepeatedEvent_IsTenantUserScopedAndCreatesOneJobAndEachActionOnce(string itemId, int actionCount)
    {
        var request = Request(FavoriteOperation.Favorite, "source-revision-1") with { ItemId = itemId };
        var first = await _pipeline.RecordAsync(request);
        var repeated = await _pipeline.RecordAsync(request);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.EventId, repeated.EventId);
        Assert.Equal(first.JobId, repeated.JobId);
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Single(await database.Set<FavoriteEventRecord>().ToListAsync());
        Assert.Equal(actionCount, await database.Set<FavoriteActionRecord>().CountAsync());
        Assert.Single(await database.Jobs.Where(item => item.Type == FavoriteActionPipeline.JobType).ToListAsync());
        Assert.Empty(await database.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Restart_RecoversPendingEventAndCompletesVirtualLikedStateOnce()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "restart-revision"));

        // Recreate every process-local service while retaining only the durable database.
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
    public async Task FavoriteLifecycle_WritesScopedRecommendationSignalsThatCancelOnUnfavorite()
    {
        var backendIdentityId = Guid.CreateVersion7();
        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.BackendIdentities.Add(new BackendIdentityRecord
            {
                Id = backendIdentityId,
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
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                OwnerUserId = _userId,
                BackendIdentityId = backendIdentityId,
                LibraryScopeId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                BackendItemId = "local-track",
                FilePath = "/read-only/library/track.flac",
                Title = "Fixture",
                Artist = "Artist",
                DurationMilliseconds = 180_000,
                ProviderIdsJson = "{\"fixture\":\"track-1\"}",
                IndexedAt = _clock.UtcNow,
                SourceModifiedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            database.IntelligencePolicies.Add(new IntelligencePolicyRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                OwnerUserId = _userId,
                Protocol = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                LibraryScopeId = "music",
                Enabled = true,
                AllowedSignalTypesJson = "[\"favorite\"]",
                EnabledProvidersJson = "[]",
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                Revision = 1
            });
            await database.SaveChangesAsync();
        }
        var signals = new RecommendationSignalWriter(_factory, _clock);
        var handler = new FavoriteActionJobHandler(_factory, [], _clock, signals);

        await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "signal-v1", libraryScopeId: "music"));
        var favorite = await _jobs.ClaimNextAsync("favorite-signal", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(favorite);
        await _jobs.CompleteAsync(favorite!,
            await handler.ExecuteAsync(new DurableJobExecutionContext(favorite!, EmptyServices.Instance), default));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _pipeline.RecordAsync(Request(FavoriteOperation.Unfavorite, "signal-v1", libraryScopeId: "music"));
        var unfavorite = await _jobs.ClaimNextAsync("unfavorite-signal", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(unfavorite);
        await _jobs.CompleteAsync(unfavorite!,
            await handler.ExecuteAsync(new DurableJobExecutionContext(unfavorite!, EmptyServices.Instance), default));

        await using var verified = await _factory.CreateDbContextAsync();
        var values = await verified.ListeningSignals.OrderBy(item => item.ObservedAt)
            .Select(item => item.Value).ToListAsync();
        Assert.Equal([1d, -1d], values);
        var profile = await new ListeningProfileService(_factory, _clock).BuildAsync(
            new IntelligenceScope(_tenantId, _userId, "jellyfin", "jellyfin-main", "music"));
        Assert.Equal(0, profile.FavoriteCount);
        Assert.Empty(profile.TopTrackKeys);
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
        var request = Request(FavoriteOperation.Favorite, "state-v1") with { ItemId = "ext-fixture-song-track-1" };
        var favorite = await _pipeline.RecordAsync(request);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var unstar = await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite });

        await using (var database = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(DurableJobState.Cancelled,
                (await database.Jobs.SingleAsync(item => item.Id == favorite.JobId)).State);
            var oldEvent = await database.Set<FavoriteEventRecord>().SingleAsync(item => item.Id == favorite.EventId);
            Assert.Equal(FavoriteEventState.Cancelled, oldEvent.State);
            var cancelledActions = await database.Set<FavoriteActionRecord>()
                .Where(item => item.EventId == favorite.EventId).ToListAsync();
            Assert.Equal(2, cancelledActions.Count);
            Assert.All(cancelledActions, action => Assert.Equal(FavoriteActionState.Cancelled, action.State));
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

    [Theory]
    [InlineData("ext-deezer-song-track-1", true)]
    [InlineData("  ext-deezer-song-track-1  ", true)]
    [InlineData("ext-apple-download-song-track-1", true)]
    [InlineData("ext-fixture-track-1", true)]
    [InlineData("native-track", false)]
    [InlineData("ext-fixture-playlist-list-1", false)]
    [InlineData("ext-fixture-album-album-1", false)]
    [InlineData("ext-fixture-artist-artist-1", false)]
    public async Task Favorite_QueuesDownloadsOnlyForExternalTracks(string itemId, bool download)
    {
        var request = Request(FavoriteOperation.Favorite, "track-kind", libraryScopeId: "music") with { ItemId = itemId };
        var receipt = await _pipeline.RecordAsync(request);

        await using var database = await _factory.CreateDbContextAsync();
        var actions = await database.Set<FavoriteActionRecord>()
            .Where(item => item.EventId == receipt.EventId).Select(item => item.ActionType).OrderBy(item => item).ToListAsync();
        Assert.Equal(download ? new[] { "download", "virtual-liked" } : new[] { "virtual-liked" }, actions);
    }

    [Fact]
    public async Task LegacyPolicy_CannotDisableDownloadsOrEnableRetiredActions()
    {
        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.FavoriteActionPolicies.Add(new FavoriteActionPolicyRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                Scope = FavoriteActionPolicyScope.Global,
                Protocol = "jellyfin",
                BackendInstanceId = "jellyfin-main",
                LibraryScopeId = "music",
                AddToVirtualLiked = false,
                AutoDownload = false,
                MatchLocalLibrary = true,
                PlaceManagedFile = true,
                EnrichMetadata = true,
                RefreshBackendLibrary = true,
                UpdatedByUserId = _userId,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await database.SaveChangesAsync();
        }
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "old-policy", libraryScopeId: "music")
            with
        { ItemId = "ext-fixture-song-track-1" });

        await using var verified = await _factory.CreateDbContextAsync();
        Assert.Equal(new[] { "download", "virtual-liked" }, await verified.Set<FavoriteActionRecord>()
            .Where(item => item.EventId == receipt.EventId).Select(item => item.ActionType).OrderBy(item => item).ToListAsync());
        var saved = await verified.Set<FavoriteEventRecord>().SingleAsync(item => item.Id == receipt.EventId);
        Assert.Null(saved.TargetCredentialReferenceId);
        Assert.Contains("download-only", saved.PolicySnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingRetiredActions_AreCancelledWhileDownloadAndFavoriteStateComplete()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "legacy-actions", libraryScopeId: "music")
            with
        { ItemId = "ext-fixture-song-track-1" });
        var retiredTypes = new[] { "match", "place", "enrich", "refresh", "lastfm" };
        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.Set<FavoriteActionRecord>().AddRange(retiredTypes.Select(type => new FavoriteActionRecord
            {
                Id = Guid.CreateVersion7(),
                EventId = receipt.EventId,
                TenantId = _tenantId,
                OwnerUserId = _userId,
                ActionType = type,
                IdempotencyKey = "legacy-" + type,
                State = FavoriteActionState.Pending,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            }));
            await database.SaveChangesAsync();
        }
        var calls = new List<string>();
        var executors = retiredTypes.Append("download").Select(type => new RecordingExecutor(type, calls));
        var handler = new FavoriteActionJobHandler(_factory, executors, _clock);
        var claim = await _jobs.ClaimNextAsync("legacy-actions", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(claim);
        var completion = await handler.ExecuteAsync(new DurableJobExecutionContext(claim!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(claim!, completion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(new[] { "download" }, calls);
        await using var verified = await _factory.CreateDbContextAsync();
        var actions = await verified.Set<FavoriteActionRecord>().Where(item => item.EventId == receipt.EventId).ToListAsync();
        Assert.All(actions.Where(item => retiredTypes.Contains(item.ActionType)), action =>
        {
            Assert.Equal(FavoriteActionState.Cancelled, action.State);
            Assert.Equal("favorite_action_retired", action.LastErrorCode);
            Assert.Equal(0, action.AttemptCount);
            Assert.NotNull(action.CompletedAt);
        });
        Assert.True((await verified.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task DownloadAction_ReusesOnlyAnExactOwnerBackendLibraryMatch()
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
            ActionType = "download",
            IdempotencyKey = "download-key"
        };
        var download = await new FavoriteDownloadActionExecutor(null!, _factory)
            .ExecuteAsync(favoriteEvent, action, default);
        Assert.True(download.Succeeded);

        favoriteEvent.OwnerUserId = _otherUserId;
        Assert.False(await FavoriteDownloadActionExecutor.HasLocalMatchAsync(_factory, favoriteEvent, default));
        favoriteEvent.OwnerUserId = _userId;
        favoriteEvent.LibraryScopeId = "other-library";
        Assert.False(await FavoriteDownloadActionExecutor.HasLocalMatchAsync(_factory, favoriteEvent, default));
        favoriteEvent.LibraryScopeId = "music";
        favoriteEvent.BackendInstanceId = "other-backend";
        Assert.False(await FavoriteDownloadActionExecutor.HasLocalMatchAsync(_factory, favoriteEvent, default));
    }

    [Fact]
    public async Task Download_RetriesWithoutRepeatingCompletedFavoriteState()
    {
        var pipeline = new FavoriteActionPipeline(_factory, _jobs, _clock);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "jellyfin-main", "backend-user",
            new AllstarrPrincipal(_tenantId, _userId, "jellyfin", "jellyfin-main", "backend-user", "Favorite user", false),
            "composite-retry", _clock.UtcNow.AddMinutes(5), default, libraryScopeId: "music");
        await pipeline.RecordAsync(new(context, "ext-fixture-song-track-1", FavoriteOperation.Favorite, "chain-v1"));
        var calls = new List<string>();
        var downloadAttempts = 0;
        var executors = new IFavoriteActionExecutor[]
        {
            new RecordingExecutor("download", calls, () => ++downloadAttempts == 1
                ? FavoriteActionExecutionResult.Retry("download-temporary", "Download will retry.")
                : FavoriteActionExecutionResult.Success())
        };
        var handler = new FavoriteActionJobHandler(_factory, executors, _clock);
        var first = await _jobs.ClaimNextAsync("favorite-chain-1", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(first);
        var firstCompletion = await handler.ExecuteAsync(new DurableJobExecutionContext(first!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Retry, firstCompletion.Kind);
        await _jobs.CompleteAsync(first!, firstCompletion);
        Assert.Equal(new[] { "download" }, calls);
        await using (var verified = await _factory.CreateDbContextAsync())
            Assert.Equal(1, (await verified.Set<FavoriteStateRecord>().SingleAsync()).Revision);

        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        var second = await _jobs.ClaimNextAsync("favorite-chain-2", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(second);
        var secondCompletion = await handler.ExecuteAsync(new DurableJobExecutionContext(second!, EmptyServices.Instance), default);
        await _jobs.CompleteAsync(second!, secondCompletion);

        Assert.Equal(DurableJobCompletionKind.Succeeded, secondCompletion.Kind);
        Assert.Equal(new[] { "download", "download" }, calls);
        await using var completed = await _factory.CreateDbContextAsync();
        Assert.Equal(1, (await completed.Set<FavoriteStateRecord>().SingleAsync()).Revision);
    }

    private FavoriteMutationRequest Request(FavoriteOperation operation, string revision, Guid? userId = null,
        string? libraryScopeId = null) => new(
        new ProtocolExecutionContext(ProtocolKind.Jellyfin, "jellyfin-main", "backend-user", new AllstarrPrincipal(
            _tenantId, userId ?? _userId, "jellyfin", "jellyfin-main", "backend-user", "Favorite user", false),
            $"favorite-test-{operation.ToString().ToLowerInvariant()}", _clock.UtcNow.AddMinutes(1), default,
            libraryScopeId: libraryScopeId),
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

    public async Task DisposeAsync() => await _database.DisposeAsync();

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
