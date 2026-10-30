using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData(PlaybackTransition.Stop, false, true, 0, ListeningEventState.Skipped)]
    [InlineData(PlaybackTransition.Stop, false, true, 1, ListeningEventState.Skipped)]
    [InlineData(PlaybackTransition.Stop, false, true, 20, ListeningEventState.Abandoned)]
    [InlineData(PlaybackTransition.Stop, false, false, 1, ListeningEventState.Abandoned)]
    [InlineData(PlaybackTransition.Stop, true, true, 1, ListeningEventState.Abandoned)]
    [InlineData(PlaybackTransition.InferredStop, false, true, 10, ListeningEventState.Abandoned)]
    [InlineData(PlaybackTransition.InferredStop, false, false, 10, ListeningEventState.Abandoned)]
    public async Task InterruptedPlaybackDoesNotWriteSkipSignals(PlaybackTransition transition, bool failed,
        bool started, int seconds, ListeningEventState expected)
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(writer, new Scrobbles(), new Lyrics(), factory, playbackTracks);
        if (started) await Process(Signal(PlaybackTransition.Start, "track-1", 0));
        await Process(Signal(transition, "track-1", TimeSpan.FromSeconds(seconds).Ticks) with
        { ObservedAt = clock.UtcNow.AddSeconds(1), Failed = failed });
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(expected, (await db.ListeningEvents.SingleAsync()).State);
        Assert.Equal(expected == ListeningEventState.Skipped, writer.LastType == "skip");

        async Task Process(PlaybackSignalRequest request)
        {
            Assert.True(await pipeline.RecordAsync(request));
            var claim = await jobs.ClaimNextAsync("interruptions", [PlaybackSignalPipeline.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureBeforeStopRemainsAbandoned(bool failedStart)
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        var start = Signal(PlaybackTransition.Start, "track-1", 0);
        var requests = failedStart
            ? new[] { start with { Failed = true }, start with { Transition = PlaybackTransition.Stop, ObservedAt = clock.UtcNow.AddSeconds(1) } }
            : new[] { start, start with { Transition = PlaybackTransition.InferredStop, ObservedAt = clock.UtcNow.AddSeconds(1) },
                start with { Transition = PlaybackTransition.Stop, ObservedAt = clock.UtcNow.AddSeconds(2) } };
        foreach (var request in requests)
        {
            await pipeline.RecordAsync(request);
            var claim = await jobs.ClaimNextAsync("failure", [PlaybackSignalPipeline.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task SeekRestartDoesNotBecomeASkip()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        foreach (var transition in new[] { PlaybackTransition.Start, PlaybackTransition.Stop })
        {
            await pipeline.RecordAsync(Signal(transition, "track-1", TimeSpan.FromSeconds(15).Ticks));
            var claim = await jobs.ClaimNextAsync("seek", [PlaybackSignalPipeline.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task SeekProgressCannotTurnALaterStopIntoASkip(int seekSeconds)
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        var start = Signal(PlaybackTransition.Start, "track-1", 0);
        var requests = new[]
        {
            start,
            start with { Transition = PlaybackTransition.Progress, PositionTicks = TimeSpan.FromSeconds(10).Ticks, ObservedAt = clock.UtcNow.AddSeconds(10) },
            start with { Transition = PlaybackTransition.Progress, PositionTicks = TimeSpan.FromSeconds(seekSeconds).Ticks, ObservedAt = clock.UtcNow.AddSeconds(11) },
            start with { Transition = PlaybackTransition.Stop, PositionTicks = TimeSpan.FromSeconds(seekSeconds + 1).Ticks, ObservedAt = clock.UtcNow.AddSeconds(12) }
        };
        foreach (var request in requests)
        {
            await pipeline.RecordAsync(request);
            var claim = await jobs.ClaimNextAsync("seek-progress", [PlaybackSignalPipeline.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrRestartedPlaybackCannotBecomeASkip(bool restart)
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        var start = await jobs.ClaimNextAsync("expiry", [PlaybackSignalPipeline.JobType]);
        await jobs.CompleteAsync(start!, await handler.ExecuteAsync(new(start!, EmptyServices.Instance), default));
        if (restart) await handler.RecoverAsync(default);
        else
        {
            clock.UtcNow += TimeSpan.FromHours(9);
            await new ListeningHistoryRetentionSweeper(factory, clock).SweepAsync();
        }
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(1).Ticks));
        var stop = await jobs.ClaimNextAsync("expiry", [PlaybackSignalPipeline.JobType]);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(stop!, EmptyServices.Instance), default)).Kind);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Theory]
    [InlineData(PlaybackTransition.Stop)]
    [InlineData(PlaybackTransition.InferredStop)]
    public async Task InterruptedEligibleListenStillScrobblesToBothDestinations(PlaybackTransition transition)
    {
        var lastfm = new Target("lastfm", true);
        var listenbrainz = new Target("listenbrainz", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [lastfm, listenbrainz],
            new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await delivery.DeliverAsync(Payload() with { Transition = transition, Failed = true }, default);
        Assert.Equal(1, lastfm.Successes);
        Assert.Equal(1, listenbrainz.Successes);
    }

    [Fact]
    public async Task FailedExternalProgressAbandonsTheOccurrence()
    {
        var track = new PlaybackTrackSnapshot(null, null, "external", "Track", "Artist", null, 120000);
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "ext-deezer-song-42", TimeSpan.FromSeconds(60).Ticks) with
        { Failed = true, SubmittedTrack = track });
        var claim = await jobs.ClaimNextAsync("external-failure", [PlaybackSignalPipeline.JobType]);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task QueuedPlaybackFromBeforeRestartDoesNotTeachASkip()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(writer, new Scrobbles(), new Lyrics(), factory, playbackTracks);
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", 0));
        await handler.RecoverAsync(default);
        while (await jobs.ClaimNextAsync("restart", [PlaybackSignalPipeline.JobType]) is { } claim)
        {
            var result = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim, result);
        }
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ListeningEventState.Abandoned, (await db.ListeningEvents.SingleAsync()).State);
        Assert.NotEqual("skip", writer.LastType);
    }

    [Fact]
    public async Task StreamFailureIsCapturedEvenWithoutAnOpenedSource()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var stop = Signal(PlaybackTransition.Stop, "track-1", 0);
        var execution = stop.ExecutionContext;
        activity.StreamInterrupted(new(execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default, new("client", "device")), "track-1");
        await new PlaybackSignalPipeline(jobs, activity).RecordAsync(stop with { ObservedAt = DateTimeOffset.UtcNow });
        var claim = await jobs.ClaimNextAsync("failure", [PlaybackSignalPipeline.JobType]);
        Assert.True(claim!.Payload.Deserialize<PlaybackSignalPayload>()!.Failed);
    }
}
