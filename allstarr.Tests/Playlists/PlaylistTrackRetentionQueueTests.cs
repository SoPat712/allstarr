using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PlaylistTrackRetentionQueueTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _providerAccountId = Guid.CreateVersion7();
    private readonly DateTimeOffset _now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableJobQueue _jobs = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = _tenantId,
                Slug = "playlist-retention-tests",
                Name = "Playlist retention tests",
                CreatedAt = _now
            });
            db.Users.Add(new PlatformUserRecord
            {
                Id = _userId,
                TenantId = _tenantId,
                DisplayName = "Playlist retention owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = _now,
                UpdatedAt = _now
            });
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = _providerAccountId,
                TenantId = _tenantId,
                OwnerUserId = _userId,
                ProviderId = "spotify",
                DisplayName = "Personal Spotify",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now
            });
            await db.SaveChangesAsync();
        }

        var options = new DurableJobOptions
        {
            DefaultMaxAttempts = 3,
            DefaultMaxDeferrals = 10,
            MaxPayloadBytes = 64 * 1024
        };
        _jobs = new DurableJobQueue(
            _factory,
            options,
            new JobPayloadPolicy(options),
            new FixedClock(_now));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task OnDemand_QueuesNothing()
    {
        var link = Link(PlaylistTrackRetention.OnDemand);
        var plan = Plan(link, Entry(
            "external-on-demand",
            0,
            new PlaylistResolvedRoute(
                TrackRouteKind.External,
                ProviderId: "spotify",
                ExternalId: "track-on-demand")));

        var queued = await RetentionQueue().EnqueueAsync(
            link, plan, "retention-on-demand", CancellationToken.None);

        Assert.Equal(0, queued);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task KeepAll_QueuesDistinctExternalRoutesAndSkipsLocalOrUnresolved()
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var firstSourceEntryId = Guid.CreateVersion7();
        var plan = Plan(link,
            Entry(
                firstSourceEntryId,
                0,
                new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ProviderId: "spotify",
                    ExternalId: "track-one")),
            Entry(
                "duplicate-external",
                1,
                new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ProviderId: "spotify",
                    ExternalId: "track-one")),
            Entry(
                "second-external",
                2,
                new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ProviderId: "qobuz",
                    ExternalId: "track-two")),
            Entry(
                "local",
                3,
                new PlaylistResolvedRoute(
                    TrackRouteKind.Local,
                    LibraryTrackId: Guid.CreateVersion7())),
            Entry(
                "unresolved",
                4,
                new PlaylistResolvedRoute(TrackRouteKind.Unresolved)),
            Entry(
                "missing-provider",
                5,
                new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ExternalId: "missing-provider")),
            Entry(
                "missing-external-id",
                6,
                new PlaylistResolvedRoute(
                    TrackRouteKind.External,
                    ProviderId: "spotify")));

        var queued = await RetentionQueue().EnqueueAsync(
            link, plan, "retention-distinct", CancellationToken.None);

        Assert.Equal(2, queued);
        await using var db = await _factory.CreateDbContextAsync();
        var jobs = await db.Jobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        var payloads = jobs
            .Select(item => JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(item.PayloadJson))
            .ToArray();
        var requiredPayloads = payloads.Where(item => item is not null).Select(item => item!).ToArray();
        Assert.Equal(payloads.Length, requiredPayloads.Length);
        var payloadByProvider = requiredPayloads.ToDictionary(item => item.ProviderId);
        Assert.Equal(firstSourceEntryId, payloadByProvider["spotify"].SourceEntryId);
        Assert.Equal("track-one", payloadByProvider["spotify"].ExternalId);
        Assert.Equal("track-two", payloadByProvider["qobuz"].ExternalId);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task KeepAll_StoresScopedInitiatorOnlyJobsWithStablePayloadAndIdempotency()
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var sourceEntryId = Guid.CreateVersion7();
        var plan = Plan(link, Entry(
            sourceEntryId,
            0,
            new PlaylistResolvedRoute(
                TrackRouteKind.External,
                ProviderId: "spotify",
                ExternalId: "track-stable"),
            new PlaylistSourceMetadata(
                Title: "Stable song",
                Artists: ["First artist", "Second artist"],
                Album: "Stable album")));
        var queue = RetentionQueue();

        var firstQueued = await queue.EnqueueAsync(link, plan, "retention-stable", CancellationToken.None);
        var repeatedQueued = await queue.EnqueueAsync(link, plan, "retention-stable-repeat", CancellationToken.None);

        Assert.Equal(1, firstQueued);
        Assert.Equal(0, repeatedQueued);
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(_tenantId, job.TenantId);
        Assert.Equal(_userId, job.OwnerUserId);
        Assert.Equal(link.LibraryScopeId, job.LibraryScopeId);
        Assert.Null(job.ProviderAccountId);
        Assert.Null(job.ProviderCapability);
        Assert.Equal(PlaylistTrackRetentionJobHandler.JobTypeName, job.Type);
        Assert.StartsWith("playlist-retain:", job.IdempotencyKey, StringComparison.Ordinal);

        var payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(link.Id, payload.PlaylistLinkId);
        Assert.Equal(plan.SourceSnapshotId, payload.SourceSnapshotId);
        Assert.Equal(sourceEntryId, payload.SourceEntryId);
        Assert.Equal("spotify", payload.ProviderId);
        Assert.Equal("track-stable", payload.ExternalId);
        Assert.Equal("Stable song", payload.Title);
        Assert.Equal(["First artist", "Second artist"], payload.Artists);
        Assert.Equal("Stable album", payload.Album);
        Assert.Equal(job.IdempotencyKey["playlist-retain:".Length..], payload.RetentionKey);
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task KeepAll_NullOrMalformedConstructibleSourceMetadataDoesNotBreakQueue()
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var plan = Plan(link, Entry(
            "null-metadata",
            0,
            new PlaylistResolvedRoute(
                TrackRouteKind.External,
                ProviderId: "spotify",
                ExternalId: "track-null-metadata"),
            new PlaylistSourceMetadata(Title: null, Artists: null, Album: null)));

        var queued = await RetentionQueue().EnqueueAsync(
            link, plan, "retention-null-metadata", CancellationToken.None);

        Assert.Equal(1, queued);
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking().SingleAsync();
        var payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal("Unknown track", payload.Title);
        Assert.Empty(payload.Artists);
        Assert.Null(payload.Album);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task RetentionJob_RejectsSourceMetadataOutsideTheLinkedLibraryScope()
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var snapshotId = Guid.CreateVersion7();
        var entryId = Guid.CreateVersion7();
        var metadataId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PlaylistLinks.Add(link);
            db.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
            {
                Id = snapshotId,
                TenantId = _tenantId,
                OwnerUserId = _userId,
                PlaylistLinkId = link.Id,
                ProviderAccountId = _providerAccountId,
                SnapshotVersion = 1,
                ProviderRevision = "revision-1",
                Name = "Scoped playlist",
                PayloadSha256 = new string('c', 64),
                CorrelationId = "retention-scope",
                RetrievedAt = _now,
                PublishedAt = _now
            });
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = metadataId,
                TenantId = _tenantId,
                OwnerUserId = _userId,
                ProviderAccountId = _providerAccountId,
                LibraryScopeId = "different-library",
                BackendInstanceId = link.TargetBackendInstanceId,
                BackendPrincipalId = "principal",
                Protocol = link.TargetProtocol,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = new string('d', 64),
                SnapshotVersion = 1,
                ProviderRevision = "revision-1",
                PayloadJson = "{}",
                PayloadSha256 = new string('e', 64),
                CorrelationId = "retention-scope",
                RetrievedAt = _now
            });
            db.PlaylistSourceEntries.Add(new PlaylistSourceEntryRecord
            {
                Id = entryId,
                TenantId = _tenantId,
                PlaylistSourceSnapshotId = snapshotId,
                ExternalMetadataSnapshotId = metadataId,
                SourcePosition = 0,
                SourceEntryIdHash = new string('f', 64)
            });
            await db.SaveChangesAsync();
        }

        var payload = new PlaylistTrackRetentionJobPayload(
            link.Id,
            snapshotId,
            entryId,
            "spotify",
            "track-scoped",
            new string('a', 64),
            "Scoped song",
            ["Scoped artist"],
            "Scoped album");
        var claim = new DurableJobClaim(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            PlaylistTrackRetentionJobHandler.JobTypeName,
            JsonSerializer.SerializeToElement(payload),
            _tenantId,
            _userId,
            null,
            link.LibraryScopeId,
            null,
            JsonSerializer.SerializeToElement(new { }),
            "retention-scope",
            "worker",
            _now.AddMinutes(1));
        var handler = new PlaylistTrackRetentionJobHandler(
            _factory, null!, null!, null!, null!);

        var completion = await handler.ExecuteAsync(
            new DurableJobExecutionContext(claim, NullServices.Instance),
            CancellationToken.None);

        Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal("playlist_retention_source_unavailable", completion.ErrorCode);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private PlaylistTrackRetentionQueue RetentionQueue() =>
        new(_jobs, new DurablePlaylistProjectionReader(_factory), _factory);

    private PlaylistLinkRecord Link(PlaylistTrackRetention retention) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenantId,
        OwnerUserId = _userId,
        ProviderAccountId = _providerAccountId,
        LibraryScopeId = "music-retention",
        SourceProviderId = "spotify",
        SourcePlaylistId = "playlist-retention",
        SourcePlaylistIdHash = new string('a', 64),
        TargetProtocol = "jellyfin",
        TargetBackendInstanceId = "backend",
        Mode = PlaylistLinkMode.Virtual,
        MaterializationMode = PlaylistMaterializationMode.Reconcile,
        TrackRetention = retention,
        RuleVersion = "rules-v1",
        PolicyVersion = "policy-v1",
        CreatedAt = _now,
        UpdatedAt = _now
    };

    private PlaylistMaterializationPlan Plan(
        PlaylistLinkRecord link,
        params PlaylistPreviewEntry[] entries) => new(
        PlaylistPlanMode.Virtual,
        link.Id,
        Guid.CreateVersion7(),
        "source-revision",
        false,
        new PlaylistPlanningTarget("jellyfin", "backend", null),
        new PlaylistPlanningRules("rules-v1", 1, true, false),
        new PlannedPlaylistMetadata("Retention playlist", null, null),
        "plan-idempotency",
        [],
        entries);

    private PlaylistPreviewEntry Entry(
        string sourceEntryId,
        int position,
        PlaylistResolvedRoute route,
        PlaylistSourceMetadata? metadata = null) =>
        Entry(Guid.CreateVersion7(), position, route, metadata) with
        {
            SourceTrackReference = sourceEntryId
        };

    private PlaylistPreviewEntry Entry(
        Guid sourceEntryId,
        int position,
        PlaylistResolvedRoute route,
        PlaylistSourceMetadata? metadata = null) =>
        new(
            sourceEntryId,
            position,
            $"source-{sourceEntryId:N}",
            PlaylistPreviewEntryStatus.Included,
            null,
            null,
            position,
            [],
            [],
            ResolvedRoute: route,
            TargetEligible: route.Kind == TrackRouteKind.Local,
            OutcomeCode: PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem)
        {
            SourceMetadata = metadata,
            SourceIdentity = new PlaylistSourceIdentity(
                route.ProviderId ?? "source",
                _providerAccountId,
                new string('b', 64),
                "source-revision",
                1,
                route.ExternalId)
        };

    private sealed class FixedClock(DateTimeOffset utcNow) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
