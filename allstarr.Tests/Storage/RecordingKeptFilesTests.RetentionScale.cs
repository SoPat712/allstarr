using System.Data.Common;
using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task EnablingKeepAllFor10000TracksDoesNotStallAConcurrentFavoriteWrite()
    {
        var payload = await SeedLargeRetentionPlaylistAsync();
        var initialServices = PlaylistInterestServices();
        await initialServices.Persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId,
            RetentionUpdate(0, false));
        var barrier = new PlaylistWriteBarrier();
        factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), barrier).Options);
        var (_, _, _, persistence) = PlaylistInterestServices();
        var (_, _, _, favorites) = CreateInterestPipeline();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task acceptance = Task.CompletedTask;
        Task favorite = Task.CompletedTask;
        try
        {
            acceptance = Task.Run(async () =>
                await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId,
                    RetentionUpdate(1, true), stop.Token), stop.Token);
            await barrier.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            var favoriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            favorite = Task.Run(async () =>
            {
                favoriteStarted.TrySetResult();
                await favorites.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Favorite,
                    "large-playlist-concurrent-heart"), stop.Token);
            }, stop.Token);
            await favoriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            var elapsed = Stopwatch.StartNew();
            barrier.Resume.TrySetResult();

            await favorite.WaitAsync(TimeSpan.FromSeconds(3), stop.Token);

            Assert.InRange(elapsed.Elapsed.TotalSeconds, 0, 3);
            await acceptance.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            await using var verify = new AllstarrDbContext(database.Options);
            Assert.Equal(10_000, await verify.Jobs.CountAsync(item =>
                item.Type == PlaylistTrackRetentionJobHandler.JobTypeName));
            Assert.True((await verify.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await stop.CancelAsync();
            try { await Task.WhenAll(acceptance, favorite); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task RecordingIdentityBatchUsesAConstantNumberOfReads()
    {
        await SeedLargeRetentionPlaylistAsync(100);
        Dictionary<string, Guid> expected;
        await using (var db = new AllstarrDbContext(database.Options))
            expected = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                item.ProviderId == "deezer" && item.ExternalId.StartsWith("large-track-"))
                .ToDictionaryAsync(item => "ext-deezer-song-" + item.ExternalId, item => item.CanonicalRecordingId);
        var probe = new IdentityReadProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString).AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        var ready = new DurableStorageState(database.StorageOptions);
        ready.Set(DurableStorageReadiness.Ready);
        ITrackIdentityService resolver = new TrackIdentityService(new Factory(options), ready, new SystemPlatformClock());

        var resolved = await resolver.ResolveRecordingsAsync(Actor(owner), expected.Keys.ToArray());

        Assert.Equal(expected.Count, resolved.Count);
        foreach (var (publicId, canonicalId) in expected)
            Assert.Equal(canonicalId, resolved[publicId]!.Id);
        Assert.InRange(probe.Reads, 1, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingIdentityBatchPreservesPublicIdPrecedenceBeforeProviderLookup(bool legacy)
    {
        const string typed = "ext-deezer-song-canonical-priority";
        const string old = "ext-deezer-canonical-priority";
        var id = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.CanonicalRecordings.Add(new()
            {
                Id = id,
                CreatedByUserId = owner,
                PublicId = legacy ? old : typed,
                Title = "Priority",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var ready = new DurableStorageState(database.StorageOptions);
        ready.Set(DurableStorageReadiness.Ready);
        ITrackIdentityService resolver = new TrackIdentityService(factory, ready, new SystemPlatformClock(), registry.Object);

        var result = await resolver.ResolveRecordingsAsync(Actor(owner), [typed]);

        Assert.Equal(id, result[typed]!.Id);
        Assert.Equal(id, (await resolver.ResolveRecordingAsync(Actor(owner), typed))!.Id);
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlaylistJobBatchAuthorizesAndLooksUpExistingKeysWithConstantReads()
    {
        var probe = new IdentityReadProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString).AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        var jobOptions = new DurableJobOptions();
        var jobs = new DurableJobQueue(new Factory(options), jobOptions, new JobPayloadPolicy(jobOptions), new SystemPlatformClock());
        var requests = Enumerable.Range(0, 100).Select(index => new DurableJobEnqueueRequest<BatchQueuePayload>(
            "fixture.batch", $"batch:{index}", new(index), owner, CorrelationId: "batch")).ToArray();
        await using var db = new AllstarrDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var results = await jobs.EnqueueManyInExistingTransactionAsync(db, requests);

        Assert.Equal(100, results.Count);
        Assert.All(results, result => Assert.True(result.Created));
        Assert.InRange(probe.Reads, 1, 4);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Assert.Equal(100, await db.Jobs.CountAsync(item => item.Type == "fixture.batch"));
    }

    [Fact]
    public async Task PlaylistJobBatchRejectsConflictingPayloadsForTheSamePendingKey()
    {
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var first = new DurableJobEnqueueRequest<BatchQueuePayload>(
            "fixture.binding", "same-key", new(1), owner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueManyInExistingTransactionAsync(db,
            new[] { first, first with { Payload = new(2) } }));

        await using var verify = new AllstarrDbContext(database.Options);
        Assert.False(await verify.Jobs.AnyAsync(item => item.Type == "fixture.binding"));
    }

    [Fact]
    public async Task PlaylistReferenceBatchUsesConstantReadsAndPreservesReferenceCounts()
    {
        var published = await Service().PublishAsync(Request());
        var probe = new IdentityReadProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString).AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        await using var db = new AllstarrDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var desired = Enumerable.Range(0, 100).ToDictionary(index => $"batch-interest:{index}", _ => published.ManagedFileId);
        var store = new EfManagedFileOwnershipStore(db);

        await store.StageReferencesAsync(desired, DateTimeOffset.UtcNow, default);

        Assert.InRange(probe.Reads, 1, 3);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Assert.Equal(101, (await db.ManagedFiles.AsNoTracking().SingleAsync()).ReferenceCount);
        Assert.Equal(101, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
    }

    [Fact]
    public async Task PlaylistReferenceReleaseBatchUsesConstantReadsAndPreservesOtherInterests()
    {
        var published = await Service().PublishAsync(Request());
        var desired = Enumerable.Range(0, 100).ToDictionary(index => $"batch-release:{index}", _ => published.ManagedFileId);
        await using (var seed = new AllstarrDbContext(database.Options))
        {
            await using var transaction = await seed.Database.BeginTransactionAsync();
            await new EfManagedFileOwnershipStore(seed).StageReferencesAsync(desired, DateTimeOffset.UtcNow, default);
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        Guid[] referenceIds;
        await using (var read = new AllstarrDbContext(database.Options))
            referenceIds = await read.ManagedFileReferences.Where(item => item.ReferenceKey.StartsWith("batch-release:"))
                .Select(item => item.Id).ToArrayAsync();
        var probe = new IdentityReadProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        await using var db = new AllstarrDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync();

        var released = await new EfManagedFileOwnershipStore(db)
            .StageReleaseReferencesAsync(referenceIds, DateTimeOffset.UtcNow, default);

        Assert.Equal(published.ManagedFileId, Assert.Single(released));
        Assert.InRange(probe.Reads, 1, 3);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        Assert.Equal(1, (await db.ManagedFiles.AsNoTracking().SingleAsync()).ReferenceCount);
        Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistReferenceBatchRejectsMissingOrPrivateFiles(bool privateFile)
    {
        var target = Guid.CreateVersion7();
        if (privateFile)
        {
            await using var scope = services.CreateAsyncScope();
            var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
                new(placement.RootId, Path.Combine(root, "private"), owner), artifact.SourcePath, "{title}",
                new("Private", "Artist", Extension: ".flac"), jobId, ManagedFileScopeKey.Create(owner, placement.RootId), true,
                ExpectedContentSha256: artifact.ContentSha256, ExpectedLength: artifact.Length)
            { ReferenceKey = "private-interest" });
            target = placed.File.Id;
        }
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var operation = new EfManagedFileOwnershipStore(db).StageReferencesAsync(
            new Dictionary<string, Guid> { ["batch-interest"] = target }, DateTimeOffset.UtcNow, default);
        if (privateFile) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Empty(db.ManagedFileReferences.Local);
    }

    [Fact]
    public async Task PlaylistJobBatchPersistsInBoundedCommandsWithoutSavingOtherChanges()
    {
        var probe = new IdentityReadProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        var jobOptions = new DurableJobOptions();
        var jobs = new DurableJobQueue(new Factory(options), jobOptions, new JobPayloadPolicy(jobOptions), new SystemPlatformClock());
        await using var db = new AllstarrDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        var originalName = user.DisplayName;
        user.DisplayName = "Unsaved unrelated change";
        var requests = Enumerable.Range(0, 100).Select(index => new DurableJobEnqueueRequest<BatchQueuePayload>(
            "fixture.bulk-persist", $"persist:{index}", new(index), owner, Priority: 5, MaxAttempts: 12, MaxDeferrals: 100,
            AvailableAt: DateTimeOffset.UtcNow.AddHours(1), CorrelationId: "bulk-persist")).ToArray();
        var results = await jobs.EnqueueManyInExistingTransactionAsync(db, requests);
        var before = probe.Writes;

        await jobs.PersistEnqueuedJobsAsync(db, results);

        Assert.InRange(probe.Writes - before, 1, 2);
        Assert.Equal(originalName, await db.Users.AsNoTracking().Where(item => item.Id == owner)
            .Select(item => item.DisplayName).SingleAsync());
        var rows = await db.Jobs.AsNoTracking().Where(item => item.Type == "fixture.bulk-persist")
            .OrderBy(item => item.IdempotencyKey).ToArrayAsync();
        Assert.Equal(100, rows.Length);
        foreach (var row in rows)
        {
            var index = int.Parse(row.IdempotencyKey["persist:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(index, System.Text.Json.JsonSerializer.Deserialize<BatchQueuePayload>(row.PayloadJson)!.Track);
            Assert.Equal(requests[index].AvailableAt, row.AvailableAt);
            Assert.Equal(owner, row.OwnerUserId);
            Assert.Equal(DurableJobState.Pending, row.State);
            Assert.Equal(5, row.Priority);
            Assert.Equal(12, row.MaxAttempts);
            Assert.Equal(100, row.MaxDeferrals);
        }
        Assert.All(await jobs.EnqueueManyInExistingTransactionAsync(db, requests), item => Assert.False(item.Created));
        await transaction.RollbackAsync();
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.False(await verify.Jobs.AnyAsync(item => item.Type == "fixture.bulk-persist"));
    }

    [Fact]
    public async Task PlaylistBatchRejectsMismatchedProjectionAccessBeforeReleasingInterests()
    {
        var payload = await SeedPlaylistInterestAsync();
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(other);
        var (_, _, kept, _) = PlaylistInterestServices();
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == payload.PlaylistLinkId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kept.StagePlaylistInterestsAsync(
            db, link, access, "wrong-principal", default));

        Assert.DoesNotContain(db.ManagedFileReferences.Local, item => item.ReleasedAt != null);
        await transaction.RollbackAsync();
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Equal(published.ManagedFileId, (await verify.ManagedFileReferences.SingleAsync(item =>
            item.ReleasedAt == null)).ManagedFileId);
    }

    private sealed record BatchQueuePayload(int Track);

    private sealed class IdentityReadProbe : DbCommandInterceptor
    {
        private int reads;
        private int writes;
        public int Reads => Volatile.Read(ref reads);
        public int Writes => Volatile.Read(ref writes);

        private void RecordWrite(DbCommand command)
        {
            if (command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref writes);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            RecordWrite(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RecordWrite(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref reads);
            RecordWrite(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            RecordWrite(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PlaylistWriteBarrier : DbTransactionInterceptor
    {
        private int entered;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0)
            {
                Opened.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
