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
        await using (var progressDb = factory.CreateDbContext())
        {
            var summaries = await progressDb.AuditEvents.Where(item => item.Action == "checking-matches").ToArrayAsync();
            if (force) Assert.Empty(summaries);
            else
            {
                var progress = Assert.Single(summaries);
                Assert.Equal("running", progress.Outcome);
                using var details = JsonDocument.Parse(progress.DetailsJson);
                Assert.Equal(25, details.RootElement.GetProperty("completed").GetInt32());
                Assert.Equal(26, details.RootElement.GetProperty("total").GetInt32());
            }
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Sqlite")]
    public async Task LibraryPrimaryRolloutReplacesTwoAutomaticDecisionsAndKeepsThePersonalProviderPin(bool newerSnapshot)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        var externalRecording = Guid.NewGuid();
        var snapshots = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var copies = Enumerable.Range(0, 3).Select(index =>
        {
            var copy = Local(Guid.NewGuid(), owner, "Self Love", clock.UtcNow);
            copy.BackendItemId = $"copy-{index}";
            copy.FilePath = $"/music/{index}.flac";
            copy.Album = SelfLoveMatchFixture.LibraryAlbum;
            copy.Artist = SelfLoveMatchFixture.Artist;
            copy.DurationMilliseconds = 189466;
            copy.SourceModifiedAt = clock.UtcNow.AddDays(index - 3);
            copy.CanonicalRecordingId = Guid.NewGuid();
            return copy;
        }).ToArray();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(User(owner, "Owner", clock.UtcNow));
            db.ProviderAccounts.Add(Account(account, owner, clock.UtcNow));
            db.CanonicalRecordings.AddRange(copies.Select(copy => new CanonicalRecordingRecord
            { Id = copy.CanonicalRecordingId!.Value, CreatedByUserId = owner }));
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord { Id = externalRecording, CreatedByUserId = owner });
            db.LibraryTracks.AddRange(copies);
            for (var index = 0; index < snapshots.Length; index++)
            {
                var snapshot = Snapshot(snapshots[index], account, owner, "Self Love", Hash(index), clock.UtcNow);
                snapshot.PayloadJson = JsonSerializer.Serialize(new
                {
                    Title = index == 2 ? "Self Love (Instrumental)" :
                    SelfLoveMatchFixture.Title,
                    Artist = SelfLoveMatchFixture.Artist,
                    Album = SelfLoveMatchFixture.Album,
                    DurationMilliseconds = 189466
                });
                var previousId = snapshot.Id;
                if (newerSnapshot && index < 2)
                {
                    var previous = Snapshot(Guid.NewGuid(), account, owner, "Self Love", Hash(index), clock.UtcNow.AddSeconds(-1));
                    previous.PayloadJson = snapshot.PayloadJson;
                    previousId = previous.Id;
                    snapshot.SnapshotVersion = 2;
                    db.ExternalMetadataSnapshots.Add(previous);
                }
                db.ExternalMetadataSnapshots.Add(snapshot);
                var decision = Decision(previousId, owner, null, externalRecording, TrackMatchState.Accepted, clock.UtcNow.AddMinutes(-1));
                decision.MatcherVersion = "library-primary-v29";
                db.TrackMatches.Add(decision);
            }
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = snapshots[2],
                SourceProviderId = "spotify",
                SourceExternalIdHash = Hash(2),
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "apple-musickit",
                TargetExternalId = "instrumental",
                Revision = 1,
                DecisionVersion = 1,
                Reason = "Personal choice",
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var rematches = new TrackRematchAllService(factory, Queue(factory, clock), clock);
        Assert.Equal(1, await rematches.QueueAlgorithmUpgradesAsync());
        DurableJobClaim claim;
        await using (var db = factory.CreateDbContext()) claim = RolloutClaim(await db.Jobs.SingleAsync());
        Assert.Equal(2, claim.Payload.Deserialize<TrackRematchAllJobPayload>()!.ApprovedCount);
        var commands = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), clock, new TestBackendLibraryAccess(factory, "music"));
        var handler = new TrackRematchAllJobHandler(factory, rematches, commands, clock);
        var progress = new List<DurableJobProgressUpdate>();
        var execution = new DurableJobExecutionContext(claim, EmptyServices.Instance)
        { ReportProgressAsync = (update, _) => { progress.Add(update); return Task.FromResult(true); } };

        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        Assert.Equal("Now plays from your library: 2 songs", progress.Last().Message);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(execution, default)).Kind);
        await using var saved = factory.CreateDbContext();
        var latest = await saved.TrackMatches.Where(item => item.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion).ToArrayAsync();
        Assert.Equal(2, latest.Length);
        var summary = Assert.Single(await saved.AuditEvents.Where(item => item.Action == "checking-matches").ToArrayAsync());
        Assert.Equal("succeeded", summary.Outcome);
        using var details = JsonDocument.Parse(summary.DetailsJson);
        Assert.Equal("2 of 2 · Now plays from your library: 2 songs", details.RootElement.GetProperty("message").GetString());
        Assert.All(latest, item => { Assert.Equal(TrackMatchState.Accepted, item.State); Assert.Equal(copies[2].Id, item.LibraryTrackId); });
        Assert.Equal(5, await saved.TrackMatches.CountAsync());
        var pin = Assert.Single(await saved.ManualTrackOverrides.ToArrayAsync());
        Assert.Equal("instrumental", pin.TargetExternalId);
        Assert.Equal(1, pin.Revision);
        Assert.Null(pin.RevokedAt);
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
