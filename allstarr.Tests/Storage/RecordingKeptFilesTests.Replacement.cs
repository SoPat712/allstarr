using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeepingTheSameBytesAfterGraceDeletionPreservesTheOldRemovalRecord()
    {
        var published = await PublishInitialFavoriteAsync();
        var (clock, jobs, kept, pipeline) = CreateInterestPipeline();
        await pipeline.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "replace-off"));
        clock.UtcNow = clock.UtcNow.AddHours(24);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("replace", [RecordingKeptFiles.KeptCleanupJobType]));
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await kept.CleanupAsync(claim)).Kind);
        Assert.False(File.Exists(published.Path));

        await using var scope = services.CreateAsyncScope();
        var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, placement.RootPath, null), artifact.SourcePath, placement.PathTemplate,
            Request().Track, null, ManagedFileScopeKey.Create(null, placement.RootId), true)
        { ReferenceKey = "keep:replacement", DestinationIsImmutable = true });

        Assert.NotEqual(published.ManagedFileId, placed.File.Id);
        Assert.NotEqual(published.Path, placed.File.CanonicalPath);
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.True(await scope.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
            .ResumeUnpublishedRemovalAsync(published.ManagedFileId,
                ManagedFileScopeKey.Create(null, placement.RootId)));
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        var db = scope.ServiceProvider.GetRequiredService<allstarr.Core.Storage.AllstarrDbContext>();
        Assert.Equal(2, await db.ManagedFiles.CountAsync());
        Assert.NotNull((await db.ManagedFiles.SingleAsync(item => item.Id == published.ManagedFileId)).RemovedAt);
        Assert.Null((await db.ManagedFiles.SingleAsync(item => item.Id == placed.File.Id)).RemovedAt);
    }
}
