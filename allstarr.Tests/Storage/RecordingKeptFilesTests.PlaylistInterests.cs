using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task DisablingKeepAllDoesNotFetchBackendPermissions()
    {
        var payload = await SeedPlaylistInterestAsync();
        await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, _, kept, _) = PlaylistInterestServices();
        var access = new Mock<IBackendLibraryAccessResolver>(MockBehavior.Strict);
        var persistence = new PlaylistPersistenceService(factory, new ProviderAccountResolver(factory),
            new allstarr.Core.Operations.SystemPlatformClock(), null!, access.Object, kept: kept);
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        access.VerifyNoOtherCalls();
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
    }

    [Fact]
    public async Task ImportedPrivateAccountRetirementDoesNotLoseTheOwnedKeptRecording()
    {
        await identities.LinkAsync(SourceContext(), new(recordingId,
            new("deezer", ProviderResourceKind.Track, "source-track"), ProviderIdentityScope.Account,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));
        await using (var scopeIdentity = new AllstarrDbContext(database.Options))
            await scopeIdentity.ProviderTrackIdentities.Where(item => item.Scope == ProviderIdentityScope.Catalog).ExecuteDeleteAsync();
        var payload = await SeedPlaylistInterestAsync(privateSource: true);
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, jobs, kept, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await using (var retire = new AllstarrDbContext(database.Options))
        {
            foreach (var accountRow in await retire.ProviderAccounts.ToArrayAsync()) accountRow.Enabled = false;
            await retire.SaveChangesAsync();
        }
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(1, true));
        await using (var accepted = new AllstarrDbContext(database.Options))
        {
            var reference = Assert.Single(await accepted.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
            Assert.Equal(published.ManagedFileId, reference.ManagedFileId);
            Assert.Equal(PlaylistRequest(payload).ReferenceKey, reference.ReferenceKey);
            Assert.Equal(DurableJobState.Cancelled, (await accepted.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
        }
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("private-reuse", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        var reused = Assert.IsType<KeptRecordingFile>(await kept.RetainPlaylistAsync(claim));
        Assert.Equal(published.ManagedFileId, reused.ManagedFileId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("rematch")]
    public async Task DisabledOwnerStopsInFlightPlaylistChangesWithoutReleasingSavedInterest(string action)
    {
        var payload = await SeedPlaylistInterestAsync();
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (clock, _, kept, persistence) = PlaylistInterestServices();
        Guid externalId;
        await using (var disable = new AllstarrDbContext(database.Options))
        {
            (await disable.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            externalId = await disable.ExternalMetadataSnapshots.Select(item => item.Id).SingleAsync();
            await disable.SaveChangesAsync();
        }
        var access = new TestBackendLibraryAccess(factory, "music");
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), clock, access);
        var orchestration = new PlaylistOrchestrationService(factory, null!, null!, new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), matches, clock, new KeyedAsyncLock(), access, kept: kept);
        Func<Task> operation = action switch
        {
            "update" => async () => { await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false)); }
            ,
            "delete" => () => persistence.DeleteLinkAsync(PlaylistContext(), payload.PlaylistLinkId, 0),
            _ => () => orchestration.PublishExistingDecisionsAsync(PlaylistContext(), payload.PlaylistLinkId, 0,
                payload.SourceSnapshotId, [externalId])
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(operation);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(PlaylistTrackRetention.KeepAll, (await db.PlaylistLinks.SingleAsync()).TrackRetention);
        Assert.Equal(published.ManagedFileId, Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null)
            .ToArrayAsync()).ManagedFileId);
        Assert.Empty(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
    }

    [Fact]
    public async Task RetentionFollowupUsesTheSavedLinkAndPublishedRouteWithoutProviderBrowse()
    {
        var payload = await SeedPlaylistInterestAsync();
        PlaylistLinkRecord staleLink;
        await using (var seed = new AllstarrDbContext(database.Options)) staleLink = await seed.PlaylistLinks.AsNoTracking().SingleAsync();
        var (_, jobs, _, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(1, true));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var access = new TestBackendLibraryAccess(factory, "music");
        var retention = new PlaylistTrackRetentionQueue(jobs, new(factory, access, gateway.Object), factory);
        Assert.Equal(0, await retention.EnqueuePublishedAsync(staleLink, "followup", default));
        gateway.VerifyNoOtherCalls();
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName &&
            item.State == DurableJobState.Pending).ToArrayAsync());
    }

    [Fact]
    public async Task RefreshPublishesTheNewGenerationAndReleasesRemovedPlaylistInterestsTogether()
    {
        var payload = await SeedPlaylistInterestAsync();
        var original = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (clock, _, kept, _) = PlaylistInterestServices();
        var source = new Mock<IProviderPlaylistSourceGateway>(MockBehavior.Strict);
        source.Setup(item => item.CollectAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectedPlaylistSourceSnapshot("deezer", account, PlaylistHash("playlist"), "empty-second", null,
                "Empty now", null, null, []));
        var access = new TestBackendLibraryAccess(factory, "music");
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), clock, access);
        var orchestration = new PlaylistOrchestrationService(factory, source.Object, null!, new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), matches, clock, new KeyedAsyncLock(), access, kept: kept);
        var result = await orchestration.RefreshAsync(PlaylistContext(), payload.PlaylistLinkId);
        Assert.NotEqual(payload.SourceSnapshotId, result.SnapshotId);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.NotNull((await db.PlaylistSourceSnapshots.SingleAsync(item => item.Id == result.SnapshotId)).PublishedAt);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.NotNull((await db.Jobs.SingleAsync(item => item.Id == jobId)).CancellationRequestedAt);
        var cleanup = Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(original.ManagedFileId, JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson)!.ManagedFileId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisablingOrDeletingKeepAllReleasesOnlyItsInterestAndCancelsWork(bool delete, bool otherInterest)
    {
        var payload = await SeedPlaylistInterestAsync();
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        if (otherInterest)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>().AddReferenceAsync(published.ManagedFileId,
                new(Guid.CreateVersion7(), published.ManagedFileId, null, ManagedFileScopeKey.Create(null, placement.RootId),
                    "another-interest", DateTimeOffset.UtcNow), default);
        }
        var (clock, _, _, persistence) = PlaylistInterestServices();
        if (delete) await persistence.DeleteLinkAsync(PlaylistContext(), payload.PlaylistLinkId, 0);
        else await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.DoesNotContain(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync(),
            item => item.ReferenceKey == PlaylistRequest(payload).ReferenceKey);
        var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
        Assert.NotNull(job.CancellationRequestedAt);
        Assert.Equal(DurableJobState.Running, job.State);
        var cleanups = await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync();
        if (otherInterest)
        {
            Assert.Empty(cleanups);
            Assert.Equal("another-interest", Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null)
                .ToArrayAsync()).ReferenceKey);
        }
        else
        {
            var cleanup = Assert.Single(cleanups);
            Assert.Equal(clock.UtcNow.AddHours(24), cleanup.AvailableAt);
            Assert.Equal(published.ManagedFileId, JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson)!.ManagedFileId);
        }
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    public async Task ReenabledKeepAllProtectsTheExistingCopyBeforeItsFreshWorkerRuns()
    {
        var payload = await SeedPlaylistInterestAsync();
        var published = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, _, _, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(1, true));
        await using var db = new AllstarrDbContext(database.Options);
        var reference = Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(published.ManagedFileId, reference.ManagedFileId);
        Assert.Equal(PlaylistRequest(payload).ReferenceKey, reference.ReferenceKey);
        Assert.Equal(DurableJobState.Cancelled, (await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
        var pending = Assert.Single(await db.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName &&
            item.State == DurableJobState.Pending).ToArrayAsync());
        Assert.Equal(payload.SourceSnapshotId, JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(pending.PayloadJson)!.SourceSnapshotId);
    }

    [Fact]
    public async Task FailedKeepAllAcceptanceRollsBackLinkReferenceCleanupAndFreshWorkTogether()
    {
        var payload = await SeedPlaylistInterestAsync();
        await PlaylistService().PublishAsync(PlaylistRequest(payload));
        var (_, _, _, persistence) = PlaylistInterestServices();
        await persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId, RetentionUpdate(0, false));
        await using (var failure = new AllstarrDbContext(database.Options))
            await failure.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_playlist_queue BEFORE INSERT ON durable_jobs
                WHEN NEW.Type = 'playlist.retain-track'
                BEGIN SELECT RAISE(ABORT, 'fixture queue unavailable'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => persistence.UpdateLinkAsync(PlaylistContext(), payload.PlaylistLinkId,
            RetentionUpdate(1, true)));
        await using var db = new AllstarrDbContext(database.Options);
        var link = await db.PlaylistLinks.SingleAsync();
        Assert.Equal(PlaylistTrackRetention.OnDemand, link.TrackRetention);
        Assert.Equal(1, link.Revision);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.KeptCleanupJobType)).State);
        Assert.Equal(1, await db.Jobs.CountAsync(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName));
    }

    private (InterestClock Clock, DurableJobQueue Jobs, RecordingKeptFiles Kept, PlaylistPersistenceService Persistence)
        PlaylistInterestServices()
    {
        var clock = new InterestClock(DateTimeOffset.UtcNow);
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), clock);
        var access = new TestBackendLibraryAccess(factory, "music");
        var kept = new RecordingKeptFiles(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
            placement, identities, clock, jobs: jobs, libraryAccess: access);
        var accounts = new ProviderAccountResolver(factory);
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), accounts, clock, access);
        var persistence = new PlaylistPersistenceService(factory, accounts, clock, matches, access, kept: kept);
        return (clock, jobs, kept, persistence);
    }

    private ProtocolExecutionContext PlaylistContext() => PlaybackContext(ProtocolKind.Jellyfin, owner, CanonicalItemId());

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepublishedRouteReleasesTheObsoleteFileEvenWhenItsInterestKeyStaysTheSame(bool canonicalCorrection, bool retired)
    {
        if (retired)
        {
            await identities.LinkAsync(SourceContext(), new(recordingId,
                new("deezer", ProviderResourceKind.Track, "source-track"), ProviderIdentityScope.Account,
                ProviderIdentityVerification.Verified, "fixture-exact", 1));
            await using var accountIdentity = new AllstarrDbContext(database.Options);
            await accountIdentity.ProviderTrackIdentities.Where(item => item.Scope == ProviderIdentityScope.Catalog).ExecuteDeleteAsync();
        }
        var payload = await SeedPlaylistInterestAsync();
        var original = await PlaylistService().PublishAsync(PlaylistRequest(payload));
        if (canonicalCorrection)
        {
            var replacement = (await identities.CreateRecordingAsync(Actor(owner), "corrected-recording")).Recording.Id;
            await using var correction = new AllstarrDbContext(database.Options);
            (await correction.ProviderTrackIdentities.SingleAsync()).CanonicalRecordingId = replacement;
            (await correction.TrackMatches.SingleAsync()).CanonicalRecordingId = replacement;
            if (retired) (await correction.ProviderAccounts.SingleAsync()).Enabled = false;
            await correction.SaveChangesAsync();
        }
        else await ChangePlaylistAsync(payload, "manual-reject");
        var (clock, _, kept, _) = PlaylistInterestServices();
        var access = new TestBackendLibraryAccess(factory, "music");
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), clock, access);
        var orchestration = new PlaylistOrchestrationService(factory, null!, null!, new PlaylistMaterializationPlanner(),
            new TrackMatchDecisionEngine(), matches, clock, new KeyedAsyncLock(), access, kept: kept);
        Guid externalId;
        await using (var seed = new AllstarrDbContext(database.Options))
            externalId = await seed.ExternalMetadataSnapshots.Select(item => item.Id).SingleAsync();
        await orchestration.PublishExistingDecisionsAsync(PlaylistContext(), payload.PlaylistLinkId, 0,
            payload.SourceSnapshotId, [externalId]);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        var cleanup = Assert.Single(await db.Jobs.Where(item => item.Type == RecordingKeptFiles.KeptCleanupJobType).ToArrayAsync());
        Assert.Equal(original.ManagedFileId, JsonSerializer.Deserialize<KeptRecordingCleanupPayload>(cleanup.PayloadJson)!.ManagedFileId);
        Assert.Equal(clock.UtcNow.AddHours(24), cleanup.AvailableAt);
        Assert.Equal(audio, await File.ReadAllBytesAsync(original.Path));
    }
    private static PlaylistLinkUpdate RetentionUpdate(long revision, bool enabled) => new(revision,
        PlaylistLinkMode.Virtual, PlaylistMaterializationMode.Reconcile, "fixture", "fixture", null, null,
        false, true, true, true, true, TrackRetention: enabled ? PlaylistTrackRetention.KeepAll : PlaylistTrackRetention.OnDemand);
}
