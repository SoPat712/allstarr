using allstarr.Core.Favorites;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task DueCleanupRemovesRouteAndKeptFileButPreservesSourceArtifact()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "grace-owner-unfavorite"));

        await using (var beforeDue = new AllstarrDbContext(database.Options))
        {
            var pending = Assert.Single(await beforeDue.Jobs.Where(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
            Assert.True(pending.AvailableAt > clock.UtcNow);
            Assert.Null(await jobs.ClaimNextAsync("grace-before-due",
                [RecordingKeptFiles.KeptCleanupJobType]));
            clock.UtcNow = pending.AvailableAt;
        }

        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "grace-due", [RecordingKeptFiles.KeptCleanupJobType]));
        var completion = await kept.CleanupAsync(claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(claim, completion);

        await using var db = new AllstarrDbContext(database.Options);
        var managed = Assert.Single(await db.ManagedFiles.ToArrayAsync());
        Assert.NotNull(managed.RemovedAt);
        Assert.Equal(0, managed.ReferenceCount);
        Assert.Empty(await db.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null).ToArrayAsync());
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().Where(item =>
            item.ManagedFileId == published.ManagedFileId).ToArrayAsync());
        Assert.False(File.Exists(published.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.Equal(ProviderDownloadArtifactState.Verified,
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).State);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ReheartBeforeGraceDueCancelsCleanupAndKeepsExistingFile()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, _, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "grace-reheart-unfavorite"));

        await using var beforeDue = new AllstarrDbContext(database.Options);
        var cleanup = Assert.Single(await beforeDue.Jobs.Where(item =>
            item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Null(await jobs.ClaimNextAsync("grace-reheart-before-due",
            [RecordingKeptFiles.KeptCleanupJobType]));
        clock.UtcNow = cleanup.AvailableAt;

        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Favorite, "grace-reheart-favorite"));

        Assert.Null(await jobs.ClaimNextAsync("grace-reheart-after-reheart",
            [RecordingKeptFiles.KeptCleanupJobType]));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(DurableJobState.Cancelled,
            (await db.Jobs.SingleAsync(item => item.Id == cleanup.Id)).State);
        Assert.Single(await db.Set<RecordingKeptRouteEntity>().Where(item =>
            item.ManagedFileId == published.ManagedFileId).ToArrayAsync());
        Assert.Null((await db.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).RemovedAt);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task DisabledInitiatorDoesNotBlockDueHouseholdCleanup()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "grace-disabled-unfavorite"));

        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            await db.SaveChangesAsync();
            clock.UtcNow = (await db.Jobs.SingleAsync(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType)).AvailableAt;
        }

        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "grace-disabled", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.True((await jobs.ReauthorizeAsync(claim)).Authorized);
        var completion = await kept.CleanupAsync(claim);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(claim, completion);

        await using var verify = new AllstarrDbContext(database.Options);
        Assert.NotNull((await verify.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).RemovedAt);
        Assert.False(File.Exists(published.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task StaleCleanupClaimCannotRevokeOrDeleteAfterRecovery()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "grace-stale-unfavorite"));

        await using (var db = new AllstarrDbContext(database.Options))
        {
            clock.UtcNow = (await db.Jobs.SingleAsync(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType)).AvailableAt;
        }
        var stale = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "grace-stale-first", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.True(await jobs.RecoverInterruptedAsync() >= 1);
        var current = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "grace-stale-recovered", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(stale.JobId, current.JobId);
        Assert.Equal(stale.AttemptNumber + 1, current.AttemptNumber);

        var staleCompletion = await kept.CleanupAsync(stale);
        Assert.NotEqual(DurableJobCompletionKind.Succeeded, staleCompletion.Kind);
        Assert.True(File.Exists(published.Path));
        await using (var unchanged = new AllstarrDbContext(database.Options))
        {
            Assert.Null((await unchanged.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).RemovedAt);
        }

        var completion = await kept.CleanupAsync(current);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await jobs.CompleteAsync(current, completion);
        Assert.False(File.Exists(published.Path));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ActiveGenericManagedReferencePreservesKeptFileDuringCleanup()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(
            Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "grace-reference-unfavorite"));

        await using (var scope = services.CreateAsyncScope())
        {
            var ownership = scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>();
            await ownership.AddReferenceAsync(published.ManagedFileId, new(
                Guid.CreateVersion7(), published.ManagedFileId, null,
                ManagedFileScopeKey.Create(null, placement.RootId), "playlist:fixture:kept",
                DateTimeOffset.UtcNow), default);
        }

        await using (var db = new AllstarrDbContext(database.Options))
        {
            clock.UtcNow = (await db.Jobs.SingleAsync(item =>
                item.Type == RecordingKeptFiles.KeptCleanupJobType)).AvailableAt;
        }
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync(
            "grace-reference", [RecordingKeptFiles.KeptCleanupJobType]));
        var completion = await kept.CleanupAsync(claim);
        await jobs.CompleteAsync(claim, completion);

        await using var verify = new AllstarrDbContext(database.Options);
        var file = await verify.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId);
        Assert.Null(file.RemovedAt);
        Assert.True(File.Exists(published.Path));
        Assert.Contains("playlist:fixture:kept", await verify.ManagedFileReferences.Where(item =>
            item.ManagedFileId == published.ManagedFileId && item.ReleasedAt == null)
            .Select(item => item.ReferenceKey).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }
}
