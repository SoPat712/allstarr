using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task ASecondReleaseAtTheSameClockTimeCreatesANewCleanupLifecycle()
    {
        var published = await PublishInitialFavoriteAsync();
        var (_, _, _, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "cycle-off-1"));
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Favorite, "cycle-on-2"));
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "cycle-off-3"));
        await using var db = new AllstarrDbContext(database.Options);
        var cleanups = await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync();
        Assert.Equal(2, cleanups.Length);
        Assert.Single(cleanups, item => item.State == DurableJobState.Cancelled);
        Assert.Single(cleanups, item => item.State == DurableJobState.Pending);
        Assert.Equal(0, (await db.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).ReferenceCount);
    }

    [Fact]
    public async Task ReferenceStagingRequiresATransactionAndRollsBackWithOtherAcceptedRows()
    {
        var published = await PublishInitialFavoriteAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var store = new EfManagedFileOwnershipStore(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageReferenceAsync(
            published.ManagedFileId, "keep:rollback", DateTimeOffset.UtcNow, default));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var otherUser = await db.Users.SingleAsync(item => item.Id == other);
            otherUser.DisplayName = "Uncommitted name";
            await store.StageReferenceAsync(published.ManagedFileId, "keep:rollback", DateTimeOffset.UtcNow, default);
            Assert.Equal(EntityState.Modified, db.Entry(otherUser).State);
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
            await transaction.RollbackAsync();
        }
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Equal("Listener", (await fresh.Users.SingleAsync(item => item.Id == other)).DisplayName);
        Assert.Equal(1, (await fresh.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.DoesNotContain(await fresh.ManagedFileReferences.ToArrayAsync(), item => item.ReferenceKey == "keep:rollback");
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task OlderCleanupWaitsTwentyFourHoursFromALaterReferenceRelease()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "later-off"));
        clock.UtcNow = clock.UtcNow.AddHours(24);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("later", [RecordingKeptFiles.KeptCleanupJobType]));
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var store = new EfManagedFileOwnershipStore(db);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await store.StageReferenceAsync(published.ManagedFileId, "keep:later", clock.UtcNow, default);
            await db.SaveChangesAsync();
            clock.UtcNow = clock.UtcNow.AddHours(1);
            await store.StageReleaseAsync("keep:later", clock.UtcNow, default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var completion = await kept.CleanupAsync(claim);
        Assert.Equal(DurableJobCompletionKind.Deferred, completion.Kind);
        Assert.Equal(TimeSpan.FromHours(24), completion.RetryDelay);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        await using var fresh = new AllstarrDbContext(database.Options);
        Assert.Null((await fresh.ManagedFiles.SingleAsync()).RemovedAt);
        Assert.Single(await fresh.Set<RecordingKeptRouteEntity>().ToArrayAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ReheartAfterCleanupClaimProtectsTheFileBeforeItsWorkerRuns()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "claimed-off"));
        clock.UtcNow = clock.UtcNow.AddHours(24);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("claimed", [RecordingKeptFiles.KeptCleanupJobType]));
        await pipeline.RecordAsync(Mutation(other, CanonicalItemId(), FavoriteOperation.Favorite, "claimed-on"));
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await kept.CleanupAsync(claim)).Kind);
        await using var db = new AllstarrDbContext(database.Options);
        var job = await db.Jobs.SingleAsync(item => item.Id == claim.JobId);
        Assert.Equal(DurableJobState.Running, job.State);
        Assert.NotNull(job.CancellationRequestedAt);
        Assert.Null((await db.ManagedFiles.SingleAsync()).RemovedAt);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CleanupRecoversAcrossRevocationCommitBoundary(bool afterCommit)
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "crash-off"));
        clock.UtcNow = clock.UtcNow.AddHours(24);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("crash", [RecordingKeptFiles.KeptCleanupJobType]));
        var interceptor = new InterruptCleanupSave();
        var collection = new ServiceCollection();
        collection.AddDbContext<AllstarrDbContext>(builder =>
        {
            builder.UseSqlite(database.ConnectionString);
            if (!afterCommit) builder.AddInterceptors(interceptor);
        });
        collection.AddManagedFilePlacement();
        if (afterCommit) collection.AddScoped<IManagedFileRemovalStore>(provider =>
            new InterruptRevokedRead(provider.GetRequiredService<EfManagedFileOwnershipStore>()));
        await using var interruptedServices = collection.BuildServiceProvider();
        var interrupted = new RecordingKeptFiles(factory, artifacts,
            interruptedServices.GetRequiredService<IServiceScopeFactory>(), placement, identities, clock, jobs: jobs, audioVerifier: FixtureAudioVerifier.Instance);

        await Assert.ThrowsAsync<CleanupInterrupted>(() => interrupted.CleanupAsync(claim));
        await using (var verify = new AllstarrDbContext(database.Options))
        {
            Assert.Equal(afterCommit, (await verify.ManagedFiles.SingleAsync()).RemovedAt != null);
            Assert.Equal(afterCommit ? 0 : 1, await verify.Set<RecordingKeptRouteEntity>().CountAsync());
        }
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.True(await jobs.RecoverInterruptedAsync() >= 1);
        var resumed = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("resumed", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await kept.CleanupAsync(claim)).Kind);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(resumed)).Kind);
        Assert.False(File.Exists(published.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        if (!afterCommit) Assert.True(interceptor.SawRevocation);
    }

    private sealed class CleanupInterrupted : Exception;

    private sealed class InterruptCleanupSave : SaveChangesInterceptor
    {
        public bool SawRevocation { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (AllstarrDbContext)eventData.Context!;
            if (db.ChangeTracker.Entries<DurableJobRecord>().Any(item => item.Entity.Type == RecordingKeptFiles.KeptCleanupJobType))
            {
                SawRevocation = db.ManagedFiles.AsNoTracking().Single().RemovedAt != null &&
                    !db.Set<RecordingKeptRouteEntity>().Any();
                throw new CleanupInterrupted();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InterruptRevokedRead(IManagedFileRemovalStore inner) : IManagedFileRemovalStore
    {
        public Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);
        public Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken) => inner.MarkRemovedAsync(id, cancellationToken);
        public Task<ManagedFileRecord?> GetRevokedUnpublishedAsync(Guid id, CancellationToken cancellationToken) =>
            throw new CleanupInterrupted();
    }
}
