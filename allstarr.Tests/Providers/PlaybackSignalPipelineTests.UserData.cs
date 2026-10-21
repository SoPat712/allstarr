using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task UserData_NativeProgressKeepsExistingHistoryCompletion()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(60).Ticks));
        var claim = await jobs.ClaimNextAsync("native-history", [PlaybackSignalPipeline.JobType]);
        Assert.NotNull(claim);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        await using var db = factory.CreateDbContext();
        Assert.Equal(ListeningEventState.Completed, Assert.Single(await db.ListeningEvents.ToListAsync()).State);
    }

    [Fact]
    public async Task UserData_PauseAndSeekDoNotCountAsCompletedListens()
    {
        const string itemId = "ext-deezer-song-history";
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory,
            new PlaybackTrackResolver(factory, new TestBackendLibraryAccess(factory, "music"),
                [new BackendMetadataResolver(new("History", "Artist", "Album", null, 180))]));
        var original = Signal(PlaybackTransition.Start, itemId, 0);
        foreach (var transition in new[] { PlaybackTransition.Start, PlaybackTransition.Progress, PlaybackTransition.Progress })
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            await pipeline.RecordAsync(original with
            {
                Transition = transition,
                ObservedAt = clock.UtcNow,
                PositionTicks = transition == PlaybackTransition.Start ? 0 : TimeSpan.FromSeconds(170).Ticks
            });
            var claim = await jobs.ClaimNextAsync("history", [PlaybackSignalPipeline.JobType]);
            Assert.NotNull(claim);
            Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        }
        await using (var db = factory.CreateDbContext())
            Assert.DoesNotContain(await db.ListeningEvents.ToListAsync(), item => item.State == ListeningEventState.Completed);
        foreach (var seconds in new[] { 2, 3 })
        {
            await pipeline.RecordAsync(original with { Transition = PlaybackTransition.Submission, ObservedAt = clock.UtcNow.AddSeconds(seconds) });
            var claim = await jobs.ClaimNextAsync("history", [PlaybackSignalPipeline.JobType]);
            Assert.NotNull(claim);
            await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        }
        await using var result = factory.CreateDbContext();
        Assert.Single(await result.ListeningEvents.Where(item => item.State == ListeningEventState.Completed).ToListAsync());
    }
}
