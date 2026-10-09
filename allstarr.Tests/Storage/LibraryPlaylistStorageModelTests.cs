using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class LibraryPlaylistStorageModelTests
{
    [Fact]
    public async Task SqliteModel_PersistsScopedMatchAndOrderedPlaylistEvidence()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var libraryTrackId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var playlistSnapshotId = Guid.NewGuid();
        var sourceEntryId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        context.AddRange(
            new UserRecord { Id = userId, DisplayName = "User", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "home", BackendPrincipalId = "principal", CreatedAt = now, UpdatedAt = now },
            new ProviderAccountRecord { Id = accountId, OwnerUserId = userId, ProviderId = "spotify", DisplayName = "Mine", Enabled = true, CreatedAt = now, UpdatedAt = now },
            new CanonicalRecordingRecord { Id = recordingId, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();

        context.LibraryTracks.Add(new LibraryTrackRecord
        {
            Id = libraryTrackId,
            OwnerUserId = userId,
            CanonicalRecordingId = recordingId,
            Protocol = "jellyfin",
            BackendInstanceId = "home",
            BackendLibraryId = "music",
            BackendItemId = "item-1",
            FilePath = "/media/Music/Artist/Song.flac",
            Title = "Song",
            Artist = "Artist",
            DurationMilliseconds = 180000,
            ProviderIdsJson = "{}",
            AcceptedDecisionVersion = 1,
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        });
        context.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = externalId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            BackendInstanceId = "home",
            BackendPrincipalId = "principal",
            Protocol = "jellyfin",
            ProviderId = "spotify",
            ResourceKind = "track",
            ExternalIdHash = hash,
            SnapshotVersion = 1,
            ProviderRevision = "rev-1",
            PayloadJson = "{\"title\":\"Song\"}",
            PayloadSha256 = hash,
            CorrelationId = "correlation",
            RetrievedAt = now
        });
        context.JobSchedules.Add(new JobScheduleRecord
        {
            Id = scheduleId,
            OwnerUserId = userId,
            JobType = "playlist-sync",
            CronExpression = "0 0 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = ScheduleOverlapPolicy.Skip,
            MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
            Enabled = true,
            NextRunAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        context.TrackMatches.Add(new TrackMatchRecord
        {
            Id = matchId,
            OwnerUserId = userId,
            ExternalSnapshotId = externalId,
            LibraryTrackId = libraryTrackId,
            CanonicalRecordingId = recordingId,
            State = TrackMatchState.Accepted,
            Confidence = .98,
            Threshold = .85,
            DecisionVersion = 1,
            PolicyVersion = "match-v1",
            ReasonsJson = "[\"isrc\"]",
            CandidateResultsJson = "[]",
            WarningsJson = "[]",
            CorrelationId = "correlation",
            DecidedAt = now
        });
        context.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = linkId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            ScheduleId = scheduleId,
            SourceProviderId = "spotify",
            SourcePlaylistId = "playlist-1",
            SourcePlaylistIdHash = hash,
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "home",
            Mode = PlaylistLinkMode.Materialized,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "rules-v1",
            PolicyVersion = "policy-v1",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        context.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
        {
            Id = playlistSnapshotId,
            OwnerUserId = userId,
            PlaylistLinkId = linkId,
            ProviderAccountId = accountId,
            SnapshotVersion = 1,
            ProviderRevision = "playlist-rev-1",
            Name = "Favorites",
            Description = "A provider playlist",
            ArtworkReferenceKey = "art:provider:1",
            PayloadSha256 = hash,
            CorrelationId = "correlation",
            RetrievedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistSourceEntries.Add(new PlaylistSourceEntryRecord
        {
            Id = sourceEntryId,
            PlaylistSourceSnapshotId = playlistSnapshotId,
            ExternalMetadataSnapshotId = externalId,
            SourcePosition = 0,
            SourceEntryIdHash = hash
        });
        context.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
        {
            Id = runId,
            OwnerUserId = userId,
            PlaylistLinkId = linkId,
            PlaylistSourceSnapshotId = playlistSnapshotId,
            ScheduleId = scheduleId,
            Generation = 1,
            IdempotencyKey = "link:rev-1:rules-v1:1",
            RuleVersion = "rules-v1",
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            State = PlaylistSyncState.Running,
            TargetRevisionBefore = "target-rev-1",
            StartedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistSyncEntryResults.Add(new PlaylistSyncEntryResultRecord
        {
            Id = Guid.NewGuid(),
            PlaylistSyncRunId = runId,
            PlaylistSourceEntryId = sourceEntryId,
            TrackMatchId = matchId,
            LibraryTrackId = libraryTrackId,
            SourcePosition = 0,
            TargetPosition = 0,
            Outcome = PlaylistEntryOutcome.Reused
        });
        context.PlaylistTargetMemberships.Add(new PlaylistTargetMembershipRecord
        {
            Id = Guid.NewGuid(),
            PlaylistLinkId = linkId,
            LibraryTrackId = libraryTrackId,
            CreatedBySyncRunId = runId,
            TargetEntryId = "target-entry-1",
            LastKnownPosition = 0,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        Assert.Equal("Favorites", (await context.PlaylistSourceSnapshots.SingleAsync()).Name);
        Assert.Equal(PlaylistEntryOutcome.Reused, (await context.PlaylistSyncEntryResults.SingleAsync()).Outcome);
        Assert.Equal("/media/Music/Artist/Song.flac", (await context.LibraryTracks.SingleAsync()).FilePath);

        var targetedOverride = new ManualTrackOverrideRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            ExternalSnapshotId = externalId,
            SourceProviderId = (await context.ExternalMetadataSnapshots.SingleAsync()).ProviderId,
            SourceExternalIdHash = (await context.ExternalMetadataSnapshots.SingleAsync()).ExternalIdHash,
            LibraryTrackId = libraryTrackId,
            Decision = ManualOverrideDecision.Reject,
            Reason = "not this rendition",
            DecisionVersion = 1,
            MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
            CreatedAt = now
        };
        context.ManualTrackOverrides.Add(targetedOverride);
        await context.SaveChangesAsync();
        Assert.Equal(libraryTrackId, (await context.ManualTrackOverrides.SingleAsync()).LibraryTrackId);

        var accepted = await context.TrackMatches.SingleAsync();
        accepted.State = TrackMatchState.Suggested;
        await context.SaveChangesAsync();
        Assert.Equal(TrackMatchState.Suggested, accepted.State);
        accepted.State = TrackMatchState.Unresolved;
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.Entry(accepted).Reload();

        var localTrack = await context.LibraryTracks.SingleAsync();
        localTrack.CoverArtReference = "https://backend.example/signed?token=secret";
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.Entry(localTrack).Reload();

        context.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            PlaylistLinkId = linkId,
            ProviderAccountId = accountId,
            SnapshotVersion = 2,
            ProviderRevision = "playlist-rev-2",
            Name = "Favorites",
            ArtworkReferenceKey = "https://provider.example/signed?token=secret",
            PayloadSha256 = hash,
            CorrelationId = "correlation",
            RetrievedAt = now
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var immutableSnapshot = await context.PlaylistSourceSnapshots.SingleAsync();
        immutableSnapshot.Name = "Changed in place";
        var immutableError = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("new snapshot version", immutableError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteModel_RejectsMissingSnapshotAndInvalidAcceptedShape()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);

        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.AddRange(
            new UserRecord { Id = user, DisplayName = "User", Enabled = true, BackendType = "jellyfin", BackendInstanceId = "home", BackendPrincipalId = "principal", CreatedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();

        context.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user,
            ExternalSnapshotId = Guid.NewGuid(),
            State = TrackMatchState.Accepted,
            Confidence = .9,
            Threshold = .8,
            DecisionVersion = 1,
            PolicyVersion = "v1",
            CorrelationId = "correlation",
            DecidedAt = now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
