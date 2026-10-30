using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackStopAfterClientAbortOrUpstreamFailureLearnsOnlyTheDeliberateSkip(bool clientAbort)
    {
        var recording = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var tags = new SharedTagStore(factory, clock);
        await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("rock")], clock.UtcNow)], [], false, default);
        var taste = new TasteService(factory, tags, clock);
        var track = new PlaybackTrackSnapshot(null, recording, "track-1", "Track", "Singer", null, 200000);
        using var activity = new PlaybackDeliveryActivityStore();
        var pipeline = new PlaybackSignalPipeline(jobs, activity);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, taste: taste);
        var start = Signal(PlaybackTransition.Start, "track-1", 0) with { SubmittedTrack = track };
        await Process(start);
        var execution = start.ExecutionContext;
        var context = new ProtocolExecutionContext(execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default, new("client", "device"));
        using var cancellation = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new FailingPlaybackRead()) };
        var file = Assert.IsType<FileStreamResult>(await new ProtocolStreamingResponseAdapter().CreateAsync(http, response, default, false,
            providerStream: true, interrupted: () => activity.StreamInterrupted(context, "track-1")));
        await using var stream = file.FileStream;
        Assert.Equal(1, await stream.ReadAsync(new byte[4], cancellation.Token));
        if (clientAbort)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[4], cancellation.Token).AsTask());
        }
        else await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[4], cancellation.Token).AsTask());
        clock.UtcNow += TimeSpan.FromSeconds(40);
        await Process(start with { Transition = PlaybackTransition.Stop, PositionTicks = TimeSpan.FromSeconds(40).Ticks, ObservedAt = clock.UtcNow });
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(clientAbort ? allstarr.Core.Intelligence.ListeningEventState.Skipped : allstarr.Core.Intelligence.ListeningEventState.Abandoned,
            (await verify.ListeningEvents.SingleAsync()).State);
        var learned = (await taste.GetAsync(user)).Usually.Tags;
        if (clientAbort) Assert.True(Assert.Single(learned).Value < 0);
        else Assert.Empty(learned);

        async Task Process(PlaybackSignalRequest request)
        {
            await pipeline.RecordAsync(request);
            var claim = await jobs.ClaimNextAsync("stream-ending", [PlaybackSignalPipeline.JobType]);
            var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await jobs.CompleteAsync(claim!, result);
        }
    }

    private sealed class FailingPlaybackRead : MemoryStream
    {
        private bool delivered;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delivered) throw new IOException("Source stopped delivering audio.");
            delivered = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    [Fact]
    public async Task Taste_CompletedSubmissionBeforeStartupKeepsItsPositiveLearning()
    {
        var recording = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var tags = new SharedTagStore(factory, clock);
        await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("rock")], clock.UtcNow)], [], false, default);
        var taste = new TasteService(factory, tags, clock);
        var track = new PlaybackTrackSnapshot(null, recording, "track-1", "Track", "Singer", null, 120000);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, taste: taste);
        await handler.RecoverAsync(default);
        await new PlaybackSignalPipeline(jobs).RecordAsync(Signal(PlaybackTransition.Submission, "track-1", 0) with { SubmittedTrack = track });
        var claim = await jobs.ClaimNextAsync("submission", [PlaybackSignalPipeline.JobType]);
        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(1, Assert.Single((await taste.GetAsync(user)).Usually.Tags).Value);
    }

    [Fact]
    public async Task Taste_LearnsWithoutHistoryRetentionAndPreservesPauseAndFailureReports()
    {
        var recording = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
            await db.SaveChangesAsync();
            await db.IntelligencePolicies.Where(item => item.OwnerUserId == user)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Enabled, false));
            await db.LibraryTracks.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanonicalRecordingId, (Guid?)recording));
        }
        var tags = new SharedTagStore(factory, clock);
        await tags.SaveAsync(SharedTagStore.RecordingKey(recording), [new("provider", [new("rock")], clock.UtcNow)], [], false, default);
        var taste = new TasteService(factory, tags, clock);
        var track = new PlaybackTrackSnapshot(null, recording, "track-1", "Track", "Singer", null, 120000);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, taste: taste);
        var pipeline = new PlaybackSignalPipeline(jobs);
        foreach (var transition in new[] { PlaybackTransition.Start, PlaybackTransition.Stop })
        {
            var request = Signal(transition, "track-1", transition == PlaybackTransition.Start ? 0 : TimeSpan.FromSeconds(70).Ticks) with { SubmittedTrack = track };
            await pipeline.RecordAsync(request);
            var claim = await jobs.ClaimNextAsync("taste", [PlaybackSignalPipeline.JobType]);
            var completion = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await jobs.CompleteAsync(claim!, completion);
            clock.UtcNow += TimeSpan.FromSeconds(70);
        }
        Assert.Equal(1, Assert.Single((await taste.GetAsync(user)).Usually.Tags).Value, 3);
        await using (var db = await factory.CreateDbContextAsync()) Assert.Empty(await db.ListeningEvents.ToListAsync());
        var paused = Signal(PlaybackTransition.Progress, "track-1", 0) with { Paused = true, Failed = true };
        await pipeline.RecordAsync(paused);
        var saved = await jobs.ClaimNextAsync("taste-flags", [PlaybackSignalPipeline.JobType]);
        var payload = System.Text.Json.JsonSerializer.Deserialize<PlaybackSignalPayload>(saved!.Payload);
        Assert.True(payload!.Paused);
        Assert.True(payload.Failed);
    }
}
