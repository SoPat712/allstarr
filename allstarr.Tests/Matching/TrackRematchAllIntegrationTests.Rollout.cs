using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class TrackRematchAllIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Sqlite")]
    public async Task RematchScopeChangeBetweenBatchesOnlyFailsConfirmedRequests(bool force)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var owner = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        await SeedRolloutAsync(factory, owner, account, local, clock.UtcNow, 26);
        var rematches = new TrackRematchAllService(factory, Queue(factory, clock), clock);
        if (force)
            await rematches.QueueForceAsync(new(owner, true), await rematches.PreviewAsync(new(owner, true), owner));
        else
            Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        DurableJobClaim claim;
        await using (var db = factory.CreateDbContext())
            claim = RolloutClaim(await db.Jobs.SingleAsync());
        var payload = claim.Payload.Deserialize<TrackRematchAllJobPayload>()!;
        var commands = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), clock, new TestBackendLibraryAccess(factory, "music"));
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);
        Assert.Equal(DurableJobCompletionKind.Deferred,
            (await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default)).Kind);
        var pending = Assert.Single((await rematches.ReadWorkAsync(payload, default)).Pending);
        var refreshed = Guid.CreateVersion7();
        var current = Guid.CreateVersion7();
        var delayed = Guid.CreateVersion7();
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await using (var db = factory.CreateDbContext())
        {
            var previous = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == pending.Snapshot.Id);
            var next = Snapshot(refreshed, account, owner, "Track", previous.ExternalIdHash, clock.UtcNow);
            next.SnapshotVersion = 2;
            db.ExternalMetadataSnapshots.AddRange(next,
                Snapshot(current, account, owner, "Track", Hash(2000), clock.UtcNow),
                Snapshot(delayed, account, owner, "Track", Hash(2001), payload.SnapshotCutoff));
            db.TrackMatches.Add(Decision(delayed, owner, local, null, TrackMatchState.Accepted, payload.SnapshotCutoff));
            var decision = Decision(current, owner, local, null, TrackMatchState.Accepted, clock.UtcNow);
            decision.MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion;
            db.TrackMatches.Add(decision);
            await db.SaveChangesAsync();
        }

        var completion = await handler.ExecuteAsync(new(claim with { AttemptNumber = 2 }, EmptyServices.Instance), default);

        if (force)
        {
            Assert.Equal(DurableJobCompletionKind.Failed, completion.Kind);
            Assert.Equal("track_rematch_scope_changed", completion.ErrorCode);
        }
        else
        {
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            Assert.Empty((await rematches.ReadWorkAsync(payload, default)).Pending);
            await using var db = factory.CreateDbContext();
            Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion,
                (await db.TrackMatches.SingleAsync(item => item.ExternalSnapshotId == refreshed)).MatcherVersion);
            Assert.Single(await db.TrackMatches.Where(item => item.ExternalSnapshotId == current).ToArrayAsync());
            Assert.Equal(28, await db.TrackMatches.CountAsync(item => item.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion));
        }
    }

    [Theory]
    [InlineData(DurableJobState.Failed)]
    [InlineData(DurableJobState.Cancelled)]
    [Trait("Category", "Sqlite")]
    public async Task AlgorithmRolloutRescansAfterTerminalJobsAndRestartThenStopsQueuingWhenCurrent(DurableJobState terminalState)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var owner = Guid.CreateVersion7();
        await SeedRolloutAsync(factory, owner, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.UtcNow, 1);
        var rematches = new TrackRematchAllService(factory, Queue(factory, clock), clock);
        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync();
            job.State = terminalState;
            job.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync();
        }
        var timers = new ManualTimeProvider(clock.UtcNow);
        var storage = new DurableStorageState(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready);
        using var service = new TrackMatchAlgorithmRolloutService(rematches, storage,
            NullLogger<TrackMatchAlgorithmRolloutService>.Instance, timeProvider: timers);
        await service.StartAsync(default);
        try
        {
            Assert.Equal(TimeSpan.FromMinutes(10), await timers.NextTimerAsync());
            await using (var db = factory.CreateDbContext())
            {
                var jobs = await db.Jobs.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArrayAsync();
                Assert.Equal(2, jobs.Length);
                Assert.NotEqual(jobs[0].IdempotencyKey, jobs[1].IdempotencyKey);
                Assert.Single(jobs, item => item.State == DurableJobState.Pending);
                Assert.True(jobs[1].Priority < 0);
            }
            timers.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(TimeSpan.FromMinutes(10), await timers.NextTimerAsync());
            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(2, await db.Jobs.CountAsync());
                var job = await db.Jobs.SingleAsync(item => item.State == DurableJobState.Pending);
                job.State = terminalState;
                job.CompletedAt = clock.UtcNow;
                await db.SaveChangesAsync();
            }
            timers.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(TimeSpan.FromMinutes(10), await timers.NextTimerAsync());
            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(3, await db.Jobs.CountAsync());
                var job = await db.Jobs.SingleAsync(item => item.State == DurableJobState.Pending);
                job.State = DurableJobState.Succeeded;
                job.CompletedAt = clock.UtcNow;
                (await db.TrackMatches.SingleAsync()).MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion;
                await db.SaveChangesAsync();
            }
            timers.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(TimeSpan.FromMinutes(10), await timers.NextTimerAsync());
            Assert.Equal(0, await new TrackRematchAllService(factory, Queue(factory, clock), clock).QueueAlgorithmUpgradesAsync());
            await using var final = factory.CreateDbContext();
            Assert.Equal(3, await final.Jobs.CountAsync());
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task AlgorithmRolloutQueuesOnlyOneOwnerAtATimeAndSkipsPinnedDecisions()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await SeedRolloutAsync(factory, first, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.UtcNow, 2);
        await SeedRolloutAsync(factory, second, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.UtcNow, 1);
        await using (var db = factory.CreateDbContext())
        {
            (await db.TrackMatches.FirstAsync(item => item.OwnerUserId == first)).State = TrackMatchState.Pinned;
            await db.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, Queue(factory, clock), clock);
        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        Assert.Equal(0, await rematches.QueueAlgorithmUpgradesAsync());
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync();
            var payload = JsonSerializer.Deserialize<TrackRematchAllJobPayload>(job.PayloadJson)!;
            Assert.Single((await rematches.ReadWorkAsync(payload, default)).Pending);
            job.State = DurableJobState.Succeeded;
            foreach (var decision in await db.TrackMatches.Where(item => item.OwnerUserId == job.OwnerUserId &&
                         item.State != TrackMatchState.Pinned).ToArrayAsync())
                decision.MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        await using var final = factory.CreateDbContext();
        Assert.Equal(2, await final.Jobs.CountAsync());
        Assert.Equal(2, await final.Jobs.Select(item => item.OwnerUserId).Distinct().CountAsync());
        Assert.Equal("retired-v1", (await final.TrackMatches.SingleAsync(item => item.State == TrackMatchState.Pinned)).MatcherVersion);
    }

    private static async Task SeedRolloutAsync(DbFactory factory, Guid owner, Guid account, Guid local,
        DateTimeOffset now, int count)
    {
        await using var db = factory.CreateDbContext();
        db.Users.Add(User(owner, "Owner", now));
        db.ProviderAccounts.Add(Account(account, owner, now));
        db.LibraryTracks.Add(Local(local, owner, "Track", now));
        for (var index = 0; index < count; index++)
        {
            var snapshot = Guid.CreateVersion7();
            db.ExternalMetadataSnapshots.Add(Snapshot(snapshot, account, owner, "Track", Hash(index), now));
            db.TrackMatches.Add(Decision(snapshot, owner, local, null, TrackMatchState.Accepted, now));
        }
        await db.SaveChangesAsync();
    }

    private static DurableJobClaim RolloutClaim(DurableJobRecord job) => new(job.Id, 1, job.Type,
        JsonDocument.Parse(job.PayloadJson).RootElement.Clone(), job.OwnerUserId,
        null, null, JsonSerializer.SerializeToElement(new { }), job.CorrelationId);
}
