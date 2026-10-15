using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeRecordsQueuedIntentAndPreservesEventsJobsAndCurrentStates()
    {
        const string before = "20261015072034_HouseholdKeptRecordingRoutes";
        await using var db = await _factory.CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>();
        var first = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "upgrade-first"));
        var claim = (await _jobs.ClaimNextAsync("upgrade-first"))!;
        var handler = new FavoriteActionJobHandler(_factory, [], _clock);
        await _jobs.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var pending = await _pipeline.RecordAsync(Request(FavoriteOperation.Unfavorite, "upgrade-pending"));
        var missingState = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "upgrade-missing", _otherUserId));
        var stable = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "upgrade-stable") with { ItemId = "native-stable" });
        // Reproduce the old representation: an applied state plus a newer queued
        // event, and another accepted event which has not created any state yet.
        db.ChangeTracker.Clear();
        var old = await db.Set<FavoriteStateRecord>().SingleAsync(item => item.OwnerUserId == _userId && item.ItemId != "native-stable");
        old.LastEventId = first.EventId;
        old.IsFavorite = true;
        old.Revision = 7;
        old.UpdatedAt = _clock.UtcNow.AddSeconds(-1);
        var oldStateId = old.Id;
        db.Remove(await db.Set<FavoriteStateRecord>().SingleAsync(item => item.OwnerUserId == _otherUserId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stableBefore = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleAsync(item => item.LastEventId == stable.EventId);
        var jobsBefore = await db.Jobs.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.State, item.PayloadJson, item.OwnerUserId, item.PolicySnapshotJson, item.Revision }).ToArrayAsync();
        var eventsBefore = await db.Set<FavoriteEventRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.State, item.ItemId, item.EventKey, item.PolicySnapshotJson, item.Revision }).ToArrayAsync();

        await migrator.MigrateAsync(before);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var updated = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleAsync(item => item.Id == oldStateId);
        Assert.False(updated.IsFavorite);
        Assert.Equal(pending.EventId, updated.LastEventId);
        Assert.Equal(8, updated.Revision);
        var inserted = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleAsync(item => item.OwnerUserId == _otherUserId);
        Assert.True(inserted.IsFavorite);
        Assert.Equal(missingState.EventId, inserted.LastEventId);
        Assert.Equal(1, inserted.Revision);
        var unchanged = await db.Set<FavoriteStateRecord>().AsNoTracking().SingleAsync(item => item.Id == stableBefore.Id);
        Assert.Equal(stableBefore.LastEventId, unchanged.LastEventId);
        Assert.Equal(stableBefore.Revision, unchanged.Revision);
        Assert.Equal(stableBefore.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(jobsBefore, await db.Jobs.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.State, item.PayloadJson, item.OwnerUserId, item.PolicySnapshotJson, item.Revision }).ToArrayAsync());
        Assert.Equal(eventsBefore, await db.Set<FavoriteEventRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.State, item.ItemId, item.EventKey, item.PolicySnapshotJson, item.Revision }).ToArrayAsync());
        var states = await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.LastEventId, item.IsFavorite, item.UpdatedAt, item.Revision }).ToArrayAsync();
        await migrator.MigrateAsync(before);
        await db.Database.MigrateAsync();
        Assert.Equal(states, await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.LastEventId, item.IsFavorite, item.UpdatedAt, item.Revision }).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }
}
