using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Core.Playback;

namespace allstarr.Tests;

public sealed class AdminUiHomeCountTests
{
    [Fact]
    public async Task HomeCountsSeparateWaitingRunningAndTerminalWork()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        Assert.Equal((0, 0, 0), await AdminUiController.ReadHomeJobCountsAsync(db.Jobs, now));
        foreach (var state in Enum.GetValues<DurableJobState>())
            db.Jobs.Add(Job(state, now));
        db.Jobs.Add(Job(DurableJobState.Succeeded, now.AddDays(-1)));
        db.Jobs.Add(Job(DurableJobState.Failed, now.AddDays(-1)));
        db.Jobs.Add(Job(DurableJobState.Succeeded, now.AddDays(1)));
        await db.SaveChangesAsync();

        Assert.Equal((2, 1, 1), await AdminUiController.ReadHomeJobCountsAsync(db.Jobs, now));
    }

    [Fact]
    public async Task HomeCountsPreserveTheSuppliedScopeAndUtcDayBoundary()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var now = DateTimeOffset.Parse("2026-11-06T01:00:00Z");
        var own = Job(DurableJobState.Succeeded, now.AddHours(-1));
        own.ScopeKey = "own";
        db.Jobs.AddRange(own, Job(DurableJobState.Pending, now), Job(DurableJobState.Failed, now),
            Job(DurableJobState.Succeeded, now.AddHours(-1).AddTicks(-1)));
        await db.SaveChangesAsync();

        Assert.Equal((0, 1, 0), await AdminUiController.ReadHomeJobCountsAsync(db.Jobs.Where(item => item.ScopeKey == "own"), now));
        Assert.Equal((1, 1, 1), await AdminUiController.ReadHomeJobCountsAsync(db.Jobs, now));
    }

    [Fact]
    public async Task HomeScrobblesSeparateTodayFromTheLastDayAndPreserveScope()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        foreach (var id in new[] { owner, other })
            db.Users.Add(new UserRecord { Id = id, BackendType = "Jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = id.ToString(), DisplayName = "Listener" });
        Assert.Equal((0, 0), await AdminUiController.ReadHomeScrobbleCountsAsync(db.PlaybackDeliveryCheckpoints, now));
        var ignored = Delivery(owner, now);
        ignored.State = ScopedPlaybackScrobbleOutcome.Ignored;
        var dismissed = Delivery(owner, now);
        dismissed.State = ScopedPlaybackScrobbleOutcome.Ignored;
        dismissed.ProviderCode = "dismissed";
        var notice = Delivery(owner, now);
        notice.Kind = PlaybackScrobbleDeliveryKind.NowPlaying;
        var retry = Delivery(owner, now);
        retry.State = ScopedPlaybackScrobbleOutcome.Retrying;
        db.PlaybackDeliveryCheckpoints.AddRange(Delivery(owner, now), ignored, dismissed, notice, retry,
            Delivery(owner, now.AddHours(-13)), Delivery(owner, now.AddDays(-2)), Delivery(other, now));
        await db.SaveChangesAsync();

        Assert.Equal((4, 3), await AdminUiController.ReadHomeScrobbleCountsAsync(db.PlaybackDeliveryCheckpoints, now));
        Assert.Equal((3, 2), await AdminUiController.ReadHomeScrobbleCountsAsync(db.PlaybackDeliveryCheckpoints.Where(item => item.OwnerUserId == owner), now));
    }

    private static PlaybackDeliveryCheckpointEntity Delivery(Guid owner, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = owner,
        SignalKey = Guid.NewGuid().ToString("N"),
        TargetId = "lastfm",
        UpdatedAt = at
    };

    private static DurableJobRecord Job(DurableJobState state, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = "fixture",
        ScopeKey = "household",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        State = state,
        CompletedAt = state is DurableJobState.Succeeded or DurableJobState.Failed or DurableJobState.Cancelled ? at : null,
        AvailableAt = at,
        CreatedAt = at,
        UpdatedAt = at,
        Revision = 1
    };
}
