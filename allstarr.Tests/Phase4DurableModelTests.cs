using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class Phase4DurableModelTests
{
    [Fact]
    public async Task PostgresModel_PersistsScopedMatchAndOrderedPlaylistEvidence()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);
        await context.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var backendIdentityId = Guid.NewGuid();
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
            new TenantRecord { Id = tenantId, Slug = "tenant", Name = "Tenant", CreatedAt = now },
            new PlatformUserRecord { Id = userId, TenantId = tenantId, DisplayName = "User", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now },
            new BackendIdentityRecord { Id = backendIdentityId, TenantId = tenantId, UserId = userId, BackendType = "jellyfin", BackendInstanceId = "home", PrincipalId = "principal", CreatedAt = now, LastSeenAt = now },
            new ProviderAccountRecord { Id = accountId, TenantId = tenantId, OwnerUserId = userId, ProviderId = "spotify", DisplayName = "Mine", Scope = ProviderAccountScope.User, Enabled = true, CreatedAt = now, UpdatedAt = now },
            new CanonicalRecordingRecord { Id = recordingId, TenantId = tenantId, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();

        context.LibraryTracks.Add(new LibraryTrackRecord
        {
            Id = libraryTrackId,
            TenantId = tenantId,
            OwnerUserId = userId,
            BackendIdentityId = backendIdentityId,
            CanonicalRecordingId = recordingId,
            LibraryScopeId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "home",
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
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            LibraryScopeId = "music",
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
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
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
            TenantId = tenantId,
            OwnerUserId = userId,
            ExternalSnapshotId = externalId,
            LibraryTrackId = libraryTrackId,
            CanonicalRecordingId = recordingId,
            LibraryScopeId = "music",
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
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            ScheduleId = scheduleId,
            LibraryScopeId = "music",
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
            TenantId = tenantId,
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
            TenantId = tenantId,
            PlaylistSourceSnapshotId = playlistSnapshotId,
            ExternalMetadataSnapshotId = externalId,
            SourcePosition = 0,
            SourceEntryIdHash = hash
        });
        context.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
        {
            Id = runId,
            TenantId = tenantId,
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
            TenantId = tenantId,
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
            TenantId = tenantId,
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
            TenantId = tenantId,
            OwnerUserId = userId,
            ExternalSnapshotId = externalId,
            LibraryTrackId = libraryTrackId,
            LibraryScopeId = "music",
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
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.Entry(accepted).Reload();

        var localTrack = await context.LibraryTracks.SingleAsync();
        localTrack.CoverArtReference = "https://backend.example/signed?token=secret";
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.Entry(localTrack).Reload();

        context.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
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
    public async Task PostgresModel_RejectsCrossTenantMatchAndInvalidAcceptedShape()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);
        await context.Database.MigrateAsync();

        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        context.AddRange(
            new TenantRecord { Id = tenant, Slug = "one", Name = "One", CreatedAt = now },
            new PlatformUserRecord { Id = user, TenantId = tenant, DisplayName = "User", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();

        context.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            OwnerUserId = user,
            ExternalSnapshotId = Guid.NewGuid(),
            LibraryScopeId = "music",
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
