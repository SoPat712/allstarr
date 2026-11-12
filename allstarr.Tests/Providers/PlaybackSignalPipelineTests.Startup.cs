using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("row-changed")]
    [InlineData("job-admitted")]
    public async Task StartupScrobbleRecoveryDoesNotHoldWriterDuringProviderMetadata(string concurrentChange)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks) with
            { SubmittedTrack = new(null, null, "track-1", "Track", "", null, null) });
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        var provider = new StartupMetadataGate(playbackTracks);
        var recovery = new PlaybackScrobbleRepair(factory, jobs, provider).ReconcileStartupAsync(default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string? before = null;
        try
        {
            var storage = new DurableStorageState(database.StorageOptions);
            storage.Set(DurableStorageReadiness.Ready);
            var identity = await new BackendIdentityResolver(factory, storage, new IdentityOptions(), clock)
                .ResolveAsync(new("jellyfin", "new-listener", "New listener")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(identity);
            Assert.False(recovery.IsCompleted);
            await using var db = await factory.CreateDbContextAsync();
            Assert.True(await db.Users.AnyAsync(item => item.Id == identity.UserId));
            Assert.Equal(1, provider.Calls);
            if (concurrentChange == "row-changed")
            {
                var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
                row.State = ScopedPlaybackScrobbleOutcome.Ignored;
                row.Revision++;
            }
            if (concurrentChange == "job-admitted") (await db.Jobs.SingleAsync()).State = DurableJobState.Pending;
            await db.SaveChangesAsync();
            before = JsonSerializer.Serialize(await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync());
        }
        finally
        {
            provider.Release.TrySetResult();
            await recovery;
        }
        if (concurrentChange == "unchanged") await AssertRecoveredDestinationAsync(target);
        else
        {
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(before, JsonSerializer.Serialize(await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync()));
            Assert.Single(await db.Jobs.ToArrayAsync());
        }
    }

    private sealed class StartupMetadataGate(IPlaybackTrackResolver inner) : IPlaybackTrackResolver
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<PlaybackTrackSnapshot?> ResolveAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ResolveAsync(payload, cancellationToken);
        }
    }
}
