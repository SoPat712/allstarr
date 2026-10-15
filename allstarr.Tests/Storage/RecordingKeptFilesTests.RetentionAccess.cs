using allstarr.Core.Favorites;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RetentionFollowupResolvesBackendPermissionsBeforeTakingTheWriterLock()
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(1);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        PlaylistLinkRecord link;
        await using (var db = new AllstarrDbContext(database.Options))
            link = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == playlist.PlaylistLinkId);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .Returns(async (Guid userId, CancellationToken token) =>
            {
                opened.TrySetResult();
                await resume.Task.WaitAsync(token);
                return await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(userId, token);
            });
        var retention = new PlaylistTrackRetentionQueue(jobs, new(factory, access.Object), factory, identities);
        var (_, _, _, favorites) = CreateInterestPipeline();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<int> followup = Task.FromResult(0);
        Task favorite = Task.CompletedTask;
        try
        {
            followup = Task.Run(() => retention.EnqueuePublishedAsync(link, "permission-followup", stop.Token), stop.Token);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            favorite = Task.Run(async () => await favorites.RecordAsync(Mutation(owner, CanonicalItemId(),
                FavoriteOperation.Favorite, "permission-followup-heart"), stop.Token), stop.Token);

            await favorite.WaitAsync(TimeSpan.FromSeconds(1), stop.Token);

            resume.TrySetResult();
            Assert.Equal(0, await followup.WaitAsync(TimeSpan.FromSeconds(5), stop.Token));
            await using var verify = new AllstarrDbContext(database.Options);
            Assert.True((await verify.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
            Assert.Equal(1, await verify.Jobs.CountAsync(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName));
            access.Verify(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()), Times.Once);
            access.VerifyNoOtherCalls();
        }
        finally
        {
            resume.TrySetResult();
            await stop.CancelAsync();
            try { await Task.WhenAll(followup, favorite); }
            catch (OperationCanceledException) { }
        }
    }
}
