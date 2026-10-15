using System.Data.Common;
using allstarr.Core.Favorites;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeepAllProjectionPreparationDoesNotHoldTheWriterLock()
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(100);
        var initial = PlaylistInterestServices();
        await initial.Persistence.UpdateLinkAsync(PlaylistContext(), playlist.PlaylistLinkId, RetentionUpdate(0, false));
        var barrier = new RetentionPreparationBarrier(armed: false);
        factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), barrier).Options);
        var (_, _, _, persistence) = PlaylistInterestServices();
        var (_, _, _, favorites) = CreateInterestPipeline();
        // Compile and initialize the same favorite path before measuring writer contention.
        await favorites.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Favorite, "preparation-warmup-heart"));
        await favorites.RecordAsync(Mutation(owner, CanonicalItemId(), FavoriteOperation.Unfavorite, "preparation-warmup-unheart"));
        await using (var baseline = new AllstarrDbContext(database.Options))
            Assert.False((await baseline.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
        barrier.Arm();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task acceptance = Task.CompletedTask;
        Task favorite = Task.CompletedTask;
        try
        {
            acceptance = Task.Run(async () => await persistence.UpdateLinkAsync(PlaylistContext(), playlist.PlaylistLinkId,
                RetentionUpdate(1, true), stop.Token), stop.Token);
            await barrier.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            favorite = Task.Run(async () => await favorites.RecordAsync(Mutation(owner, CanonicalItemId(),
                FavoriteOperation.Favorite, "projection-preparation-heart"), stop.Token), stop.Token);

            await favorite.WaitAsync(TimeSpan.FromSeconds(1), stop.Token);

            barrier.Resume.TrySetResult();
            await acceptance.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            await using var verify = new AllstarrDbContext(database.Options);
            Assert.True((await verify.Set<FavoriteStateRecord>().SingleAsync()).IsFavorite);
            Assert.Equal(100, await verify.Jobs.CountAsync(item => item.Type == "playlist.retain-track"));
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
    public async Task KeepAllPreparationIsRecomputedWhenAnotherConnectionChangesItsCanonicalTarget()
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(1);
        var published = await Service().PublishAsync(Request());
        var initial = PlaylistInterestServices();
        await initial.Persistence.UpdateLinkAsync(PlaylistContext(), playlist.PlaylistLinkId, RetentionUpdate(0, false));
        var boundary = new BeforeRetentionWriter(async () =>
        {
            await using var change = new AllstarrDbContext(database.Options);
            var identity = await change.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "large-track-00000");
            identity.CanonicalRecordingId = recordingId;
            identity.Revision++;
            await change.SaveChangesAsync();
        });
        factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), boundary).Options);
        var (_, _, _, persistence) = PlaylistInterestServices();

        await persistence.UpdateLinkAsync(PlaylistContext(), playlist.PlaylistLinkId, RetentionUpdate(1, true));

        Assert.Equal(1, boundary.Calls);
        await using var verify = new AllstarrDbContext(database.Options);
        var interest = await verify.ManagedFileReferences.SingleAsync(item => item.ReleasedAt == null &&
            item.ReferenceKey.StartsWith($"playlist:{playlist.PlaylistLinkId:N}:"));
        Assert.Equal(published.ManagedFileId, interest.ManagedFileId);
        Assert.Equal(2, (await verify.ManagedFiles.SingleAsync()).ReferenceCount);
    }

    private sealed class BeforeRetentionWriter(Func<Task> change) : DbTransactionInterceptor
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1) await change();
            return result;
        }
    }

    private sealed class RetentionPreparationBarrier(bool armed = true) : DbCommandInterceptor
    {
        private int entered;
        private int isArmed = armed ? 1 : 0;
        public void Arm() => Volatile.Write(ref isArmed, 1);
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref isArmed) == 1 && command.CommandText.Contains("playlist_source_entries", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref entered, 1) == 0)
            {
                Opened.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
