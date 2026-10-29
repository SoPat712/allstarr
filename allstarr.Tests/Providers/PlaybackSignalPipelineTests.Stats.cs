using allstarr.Core.Playback;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task StatsCollectWithoutPolicyAndCountOccurrencesRatherThanReports()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.IntelligencePolicies.RemoveRange(db.IntelligencePolicies);
            await db.SaveChangesAsync();
        }
        var pipeline = new PlaybackSignalPipeline(jobs);
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks);
        var start = Signal(PlaybackTransition.Start, "track-1", 0);
        var stop = start with { Transition = PlaybackTransition.Stop, PositionTicks = TimeSpan.FromSeconds(60).Ticks, ObservedAt = start.ObservedAt.AddSeconds(60) };
        var signals = new[]
        {
            start, start, stop, stop,
            stop with { ObservedAt = stop.ObservedAt.AddSeconds(1) },
            start with { PlaySessionId = "repeat", ObservedAt = start.ObservedAt.AddMinutes(3) },
            stop with { PlaySessionId = "repeat", ObservedAt = stop.ObservedAt.AddMinutes(3) },
            start with { PlaySessionId = "failed" },
            stop with { PlaySessionId = "failed", PositionTicks = 0 },
            stop with { PlaySessionId = "buffer-retry", PositionTicks = TimeSpan.FromSeconds(2).Ticks }
        };
        foreach (var signal in signals)
        {
            if (!await pipeline.RecordAsync(signal)) continue;
            var claim = await jobs.ClaimNextAsync("stats", [PlaybackSignalPipeline.JobType]);
            Assert.NotNull(claim);
            Assert.Equal(allstarr.Core.Jobs.DurableJobCompletionKind.Succeeded,
                (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
            await jobs.CompleteAsync(claim!, allstarr.Core.Jobs.DurableJobCompletion.Success());
        }
        await using var verify = factory.CreateDbContext();
        var zone = TimeZoneInfo.Utc;
        var period = ListeningStatsService.CreatePeriod("all", null, clock.UtcNow.AddDays(1), zone);
        var result = await ListeningStatsService.ReadAsync(verify, new(user, "jellyfin", "backend"), period, zone, default);
        Assert.Equal(2, result.PlayCount);
        Assert.Equal(2, Assert.Single(result.TopSongs).PlayCount);
        Assert.Equal(4, await verify.ListeningEvents.CountAsync());
    }
}
