using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopiedManagedKeptAudioRecoversWithoutRewritingRoutesReferencesOrSourceRevisions()
    {
        placement.RootPath = Path.Combine(database.StorageOptions.DataDirectory, "kept");
        var recovery = ManagedCopyRecovery();
        await recovery.InitializeAsync();
        var published = await Service().PublishAsync(Request());
        Guid fileId;
        string? identity;
        string references;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var file = await db.ManagedFiles.SingleAsync();
            fileId = file.Id;
            identity = file.FileSystemFileId;
            references = JsonSerializer.Serialize(await db.ManagedFileReferences.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());
        }
        var backup = CopiedDataFolderFixture.ReplaceWithCopy(database.StorageOptions.DataDirectory);
        try
        {
            Assert.Null(await Service().FindAsync(Actor(other), recordingId));
            await recovery.InitializeAsync();
            Assert.True(await recovery.IsPendingAsync(default));
            var claim = (await queue.ClaimNextAsync("managed-copy", [ManagedStorageRecovery.JobType]))!;
            var result = await recovery.ExecuteAsync(new(claim, services), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await queue.CompleteAsync(claim, result);
            Assert.False(await recovery.IsPendingAsync(default));
            var found = await Service().FindAsync(Actor(other), recordingId);
            Assert.NotNull(found);
            Assert.Equal(published.ManagedFileId, found.ManagedFileId);
            Assert.Equal(audio, await File.ReadAllBytesAsync(found.Path));
            await using var db = new AllstarrDbContext(database.Options);
            var file = await db.ManagedFiles.SingleAsync();
            Assert.Equal(fileId, file.Id);
            Assert.NotEqual(identity, file.FileSystemFileId);
            Assert.Equal(7, (await db.ProviderAccounts.SingleAsync()).Revision);
            Assert.Equal(references, JsonSerializer.Serialize(await db.ManagedFileReferences.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()));
            Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
            Assert.Single(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
            Assert.Single(await db.Set<ProviderDownloadArtifactEntity>().ToArrayAsync());
        }
        finally { Directory.Delete(backup, true); }
    }

    private ManagedStorageRecovery ManagedCopyRecovery()
    {
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        return new(factory, services.GetRequiredService<IServiceScopeFactory>(), queue, database.StorageOptions, state, new SystemPlatformClock());
    }
}
