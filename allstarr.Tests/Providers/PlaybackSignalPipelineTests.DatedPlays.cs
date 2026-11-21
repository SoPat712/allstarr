using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    private static readonly IReadOnlyDictionary<string, string> DatedItems =
        new Dictionary<string, string> { ["track-1"] = "track-1", ["track-long"] = "track-long" };

    [Fact]
    public async Task DatedFullPlayIsCompletedAtItsOwnTimesCountsInHistoryAndScrobblesWithoutNowPlaying()
    {
        var startedAt = clock.UtcNow.AddHours(-3);
        var (results, lyrics, target) = await RecordDatedAsync([new("track-1", startedAt, 100, "offline-1")]);

        Assert.Equal("counted", Assert.Single(results).State);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.ListeningEvents.AsNoTracking().SingleAsync();
        Assert.Equal(ListeningEventState.Completed, row.State);
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), row.StartedAt!.Value.ToUnixTimeMilliseconds());
        // Dated like a live play: at the time it started.
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), row.ListenedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.FromSeconds(100).Ticks, row.PositionTicks);
        var period = ListeningStatsService.CreatePeriod("all", null, clock.UtcNow.AddDays(1), TimeZoneInfo.Utc);
        Assert.Equal(1, (await ListeningStatsService.ReadAsync(verify, new(user, "jellyfin", "backend"), period, TimeZoneInfo.Utc, default)).PlayCount);
        Assert.Equal(0, lyrics.Calls);
        Assert.Equal(1, target.Successes);
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), target.LastObservedAt!.Value.ToUnixTimeMilliseconds());
        Assert.DoesNotContain(await verify.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync(),
            item => item.Kind == PlaybackScrobbleDeliveryKind.NowPlaying);
    }

    [Fact]
    public async Task DatedPlaySentAgainIsDuplicateAndKeepsOneRow()
    {
        var play = new V2DatedPlay("track-1", clock.UtcNow.AddHours(-2), 90, "offline-1");
        var (first, _, _) = await RecordDatedAsync([play, play with { StartedAt = play.StartedAt.AddMinutes(5) }]);
        var (again, _, target) = await RecordDatedAsync([play]);

        Assert.Equal(["counted", "duplicate"], first.Select(item => item.State));
        Assert.Equal("duplicate", Assert.Single(again).State);
        Assert.Equal(0, target.Calls);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.ListeningEvents.CountAsync());
    }

    [Fact]
    public async Task DatedPlayAlreadyReportedLiveIsDuplicate()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var start = Signal(PlaybackTransition.Start, "track-1", 0);
        Assert.True(await pipeline.RecordAsync(start));
        Assert.True(await pipeline.RecordAsync(start with
        {
            Transition = PlaybackTransition.Stop,
            PositionTicks = TimeSpan.FromSeconds(80).Ticks,
            ObservedAt = start.ObservedAt.AddSeconds(80)
        }));
        await RunPlaybackJobsAsync(new Lyrics(), new Target("lastfm", true));

        var (results, _, _) = await RecordDatedAsync([new("track-1", clock.UtcNow.AddMinutes(-1), 80, start.PlaySessionId!)]);

        Assert.Equal("duplicate", Assert.Single(results).State);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.ListeningEvents.CountAsync());
    }

    [Fact]
    public async Task DatedPlayUnderThirtySecondsIsTooShortAndKeepsNothing()
    {
        var (results, _, _) = await RecordDatedAsync([new("track-1", clock.UtcNow.AddHours(-1), 20, "offline-short")]);

        Assert.Equal("tooShort", Assert.Single(results).State);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.ListeningEvents.CountAsync());
        Assert.Equal(0, await verify.Jobs.CountAsync(item => item.Type == PlaybackSignalPipeline.JobType));
    }

    [Fact]
    public async Task DatedPartialPlayIsKeptInHistoryButNotCompleted()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var template = await db.LibraryTracks.AsNoTracking().SingleAsync();
            template.Id = Guid.CreateVersion7();
            template.BackendItemId = "track-long";
            template.FilePath = "/media/long.flac";
            template.DurationMilliseconds = 300000;
            db.LibraryTracks.Add(template);
            await db.SaveChangesAsync();
        }
        var startedAt = clock.UtcNow.AddHours(-1);

        var (results, _, target) = await RecordDatedAsync([new("track-long", startedAt, 45, "offline-partial")]);

        Assert.Equal("counted", Assert.Single(results).State);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.ListeningEvents.AsNoTracking().SingleAsync();
        Assert.NotEqual(ListeningEventState.Completed, row.State);
        Assert.Null(row.ListenedAt);
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), row.StartedAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.FromSeconds(45).Ticks, row.PositionTicks);
        Assert.Equal(1, await ListeningStatsService.CountedPlays(verify, new(user, "jellyfin", "backend")).CountAsync());
        Assert.Equal(0, target.Successes);
    }

    [Fact]
    public async Task DatedPlaysRejectTheWholeRequestWhenAPlayIsOutOfRange()
    {
        var now = DateTimeOffset.UtcNow;
        var good = new V2DatedPlay("track-1", now.AddHours(-1), 100, "offline-1");
        var plays = new V2DatedPlays(null!, new PlaybackSignalPipeline(jobs), factory, TimeProvider.System);

        var future = await plays.RecordAsync(null!, DatedExecution(), new([good, good with { PlaySessionId = "offline-2", StartedAt = now.AddMinutes(10) }]));

        Assert.Equal(400, future.Status);
        Assert.True(V2DatedPlays.Valid(new([good]), now));
        Assert.True(V2DatedPlays.Valid(new([good with { StartedAt = now.AddMinutes(4) }]), now));
        Assert.False(V2DatedPlays.Valid(new([]), now));
        Assert.False(V2DatedPlays.Valid(new(Enumerable.Range(0, 51).Select(index => good with { PlaySessionId = $"p{index}" }).ToArray()), now));
        Assert.False(V2DatedPlays.Valid(new([good with { PlaySessionId = " " }]), now));
        Assert.False(V2DatedPlays.Valid(new([good with { TrackId = "" }]), now));
        Assert.False(V2DatedPlays.Valid(new([good with { PlayedSeconds = -1 }]), now));
        Assert.False(V2DatedPlays.Valid(new([good with { PlayedSeconds = 86401 }]), now));
        Assert.False(V2DatedPlays.Valid(new([good with { StartedAt = now.AddDays(-61) }]), now));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Jobs.CountAsync(item => item.Type == PlaybackSignalPipeline.JobType));
    }

    [Fact]
    public async Task DatedPlayOfAnUnknownTrackAnswersOnlyForThatEntry()
    {
        var startedAt = clock.UtcNow.AddHours(-2);
        var (results, _, _) = await RecordDatedAsync(
        [
            new(V2Ids.Track(Guid.NewGuid()), startedAt, 100, "offline-missing"),
            new("track-1", startedAt.AddMinutes(5), 100, "offline-found")
        ]);

        Assert.Equal(["unknownTrack", "counted"], results.Select(item => item.State));
        Assert.Equal(["offline-missing", "offline-found"], results.Select(item => item.PlaySessionId));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal("track-1", (await verify.ListeningEvents.AsNoTracking().SingleAsync()).TrackReference);
    }

    private ProtocolExecutionContext DatedExecution() => new(ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(user, "jellyfin", "backend", "principal", "User", false), "correlation", clock.UtcNow.AddMinutes(1), default,
        new ProtocolClientDescriptor(deviceId: "device"));

    private async Task<(IReadOnlyList<V2DatedPlayResult> Results, Lyrics Lyrics, Target Target)> RecordDatedAsync(IReadOnlyList<V2DatedPlay> plays)
    {
        var results = await new V2DatedPlays(null!, new PlaybackSignalPipeline(jobs), factory, TimeProvider.System)
            .RecordResolvedAsync(DatedExecution(), plays, DatedItems);
        var lyrics = new Lyrics();
        var target = new Target("lastfm", true);
        await RunPlaybackJobsAsync(lyrics, target);
        return (results, lyrics, target);
    }

    private async Task RunPlaybackJobsAsync(Lyrics lyrics, Target target)
    {
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            lyrics, factory, playbackTracks);
        while (await jobs.ClaimNextAsync("dated", [PlaybackSignalPipeline.JobType]) is { } claim)
        {
            var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await jobs.CompleteAsync(claim, completion);
        }
    }
}
