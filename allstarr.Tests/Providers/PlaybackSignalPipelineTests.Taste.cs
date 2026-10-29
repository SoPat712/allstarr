using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
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
