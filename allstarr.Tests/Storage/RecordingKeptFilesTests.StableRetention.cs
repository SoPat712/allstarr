using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Refreshing10000RetainedTracksPreservesUnchangedJobsAndQueuesOnlyTheAddedTrack()
    {
        var playlist = await SeedLargeRetentionPlaylistAsync();
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        Dictionary<Guid, string> original;
        await using (var read = new AllstarrDbContext(database.Options))
            original = await read.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .ToDictionaryAsync(item => item.Id, item => item.PayloadJson);
        Assert.Equal(10_000, original.Count);
        var snapshotId = await PublishReorderedRetentionSnapshotAsync(playlist, replaceFirst: true);

        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

        await using var db = new AllstarrDbContext(database.Options);
        var saved = await db.Jobs.AsNoTracking().Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
            .ToArrayAsync();
        Assert.Equal(10_001, saved.Length);
        var retained = saved.Where(item => original.ContainsKey(item.Id) && item.CancellationRequestedAt == null).ToArray();
        Assert.Equal(9_999, retained.Length);
        Assert.All(retained, item =>
        {
            Assert.Equal(original[item.Id], item.PayloadJson);
            Assert.Equal(DurableJobState.Pending, item.State);
            Assert.Equal(0, item.Revision);
        });
        var removed = Assert.Single(saved, item => original.ContainsKey(item.Id) && item.CancellationRequestedAt != null);
        Assert.Equal("large-track-00000", JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(removed.PayloadJson)!.ExternalId);
        Assert.Equal(DurableJobState.Cancelled, removed.State);
        var added = Assert.Single(saved, item => !original.ContainsKey(item.Id));
        var payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(added.PayloadJson)!;
        Assert.Equal(snapshotId, payload.SourceSnapshotId);
        Assert.Equal("source-track", payload.ExternalId);
        Assert.Equal(DurableJobState.Pending, added.State);
        Assert.Equal(0, await new PlaylistTrackRetentionQueue(jobs,
            new(factory, new TestBackendLibraryAccess(factory, "music")), factory, identities)
            .EnqueuePublishedAsync(await db.PlaylistLinks.SingleAsync(item => item.Id == playlist.PlaylistLinkId), "followup", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetentionWorkerFollowsItsUnchangedSourceAcrossReorderedSnapshots(bool freshMetadata)
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(3);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("before-refresh", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        await PublishReorderedRetentionSnapshotAsync(playlist, freshMetadata: freshMetadata);

        Assert.Null(await kept.RetainPlaylistAsync(claim));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalCorrectionReplacesThePendingJobAndRejectsItsOldTarget(bool executeOld)
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(1);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        var claim = executeOld ? Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("before-correction",
            [PlaylistTrackRetentionJobHandler.JobTypeName])) : null;
        Guid originalJob;
        await using (var change = new AllstarrDbContext(database.Options))
        {
            originalJob = await change.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .Select(item => item.Id).SingleAsync();
            var identity = await change.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "large-track-00000");
            identity.CanonicalRecordingId = recordingId;
            identity.Revision++;
            await change.SaveChangesAsync();
        }
        if (executeOld)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kept.RetainPlaylistAsync(claim!));
            return;
        }

        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

        await using var db = new AllstarrDbContext(database.Options);
        var rows = await db.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(DurableJobState.Cancelled, rows.Single(item => item.Id == originalJob).State);
        Assert.Equal(DurableJobState.Pending, rows.Single(item => item.Id != originalJob).State);
    }

    [Fact]
    public async Task CanonicalMergePreservesAnUnchangedRetentionJob()
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(1);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        Guid target;
        Guid originalJob;
        await using (var read = new AllstarrDbContext(database.Options))
        {
            target = await read.ProviderTrackIdentities.Where(item => item.ExternalId == "large-track-00000")
                .Select(item => item.CanonicalRecordingId).SingleAsync();
            originalJob = await read.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .Select(item => item.Id).SingleAsync();
        }
        await identities.MergeRecordingsAsync(Actor(owner), target, recordingId, "retention-merge");
        await PublishReorderedRetentionSnapshotAsync(playlist);

        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

        await using var verify = new AllstarrDbContext(database.Options);
        var saved = Assert.Single(await verify.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName).ToArrayAsync());
        Assert.Equal(originalJob, saved.Id);
        Assert.Null(saved.CancellationRequestedAt);
        Assert.Equal(DurableJobState.Pending, saved.State);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("after-merge", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        Assert.Null(await kept.RetainPlaylistAsync(claim));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplacementSourceInputCannotBorrowAnOldJobForTheSameProviderTrack(bool executeOld)
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(1);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        var claim = executeOld ? Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("before-input-replacement",
            [PlaylistTrackRetentionJobHandler.JobTypeName])) : null;
        Guid originalId;
        await using (var read = new AllstarrDbContext(database.Options))
            originalId = await read.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .Select(item => item.Id).SingleAsync();
        var newSnapshot = await PublishReorderedRetentionSnapshotAsync(playlist, replaceFirst: true);
        await using (var change = new AllstarrDbContext(database.Options))
        {
            var metadata = await (from source in change.PlaylistSourceEntries
                                  join row in change.ExternalMetadataSnapshots on source.ExternalMetadataSnapshotId equals row.Id
                                  where source.PlaylistSourceSnapshotId == newSnapshot
                                  select row).SingleAsync();
            change.ManualTrackOverrides.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ExternalSnapshotId = metadata.Id,
                SourceProviderId = metadata.ProviderId,
                SourceExternalIdHash = metadata.ExternalIdHash,
                TargetProviderId = "deezer",
                TargetExternalId = "large-track-00000",
                Decision = ManualOverrideDecision.Pin,
                Reason = "Replacement input",
                DecisionVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await change.SaveChangesAsync();
        }
        if (executeOld)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => kept.RetainPlaylistAsync(claim!));
            return;
        }

        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName).ToArrayAsync();
        Assert.Equal(2, saved.Length);
        Assert.Equal(DurableJobState.Cancelled, saved.Single(item => item.Id == originalId).State);
        var replacement = saved.Single(item => item.Id != originalId);
        Assert.Equal(DurableJobState.Pending, replacement.State);
        var oldPayload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(saved.Single(item => item.Id == originalId).PayloadJson)!;
        var newPayload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(replacement.PayloadJson)!;
        Assert.Equal(oldPayload.ProviderId, newPayload.ProviderId);
        Assert.Equal(oldPayload.ExternalId, newPayload.ExternalId);
        Assert.Equal(oldPayload.TargetCanonicalRecordingId, newPayload.TargetCanonicalRecordingId);
        Assert.Equal(newSnapshot, newPayload.SourceSnapshotId);
        Assert.NotEqual(oldPayload.SourceEntryId, newPayload.SourceEntryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingTheInputForAnExistingRouteWithinTheSameSnapshotQueuesFreshWork(bool runningOriginal)
    {
        var playlist = await SeedLargeRetentionPlaylistAsync(2);
        Guid[] metadataIds;
        await using (var read = new AllstarrDbContext(database.Options))
            metadataIds = await read.PlaylistSourceEntries.Where(item => item.PlaylistSourceSnapshotId == playlist.SourceSnapshotId)
                .OrderBy(item => item.SourcePosition).Select(item => item.ExternalMetadataSnapshotId).ToArrayAsync();
        await SetRetentionOverrideAsync(metadataIds[1], ManualOverrideDecision.Reject);
        var (_, jobs, kept, _) = PlaylistInterestServices();
        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);
        if (runningOriginal)
            Assert.NotNull(await jobs.ClaimNextAsync("before-switch", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        Guid originalJob;
        await using (var read = new AllstarrDbContext(database.Options))
            originalJob = await read.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .Select(item => item.Id).SingleAsync();
        await SetRetentionOverrideAsync(metadataIds[0], ManualOverrideDecision.Reject);
        await SetRetentionOverrideAsync(metadataIds[1], ManualOverrideDecision.Pin, "large-track-00000");

        await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

        await using var verify = new AllstarrDbContext(database.Options);
        var saved = await verify.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName).ToArrayAsync();
        Assert.Equal(2, saved.Length);
        var original = saved.Single(item => item.Id == originalJob);
        Assert.Equal(runningOriginal ? DurableJobState.Running : DurableJobState.Cancelled, original.State);
        Assert.NotNull(original.CancellationRequestedAt);
        var replacement = saved.Single(item => item.Id != originalJob);
        Assert.Equal(DurableJobState.Pending, replacement.State);
        var payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(replacement.PayloadJson)!;
        Assert.Equal(playlist.SourceSnapshotId, payload.SourceSnapshotId);
        Assert.NotEqual(playlist.SourceEntryId, payload.SourceEntryId);
        Assert.Equal(playlist.ExternalId, payload.ExternalId);
        for (var transition = 0; transition < 3; transition++)
        {
            var activeInput = transition % 2;
            await SetRetentionOverrideAsync(metadataIds[1 - activeInput], ManualOverrideDecision.Reject);
            await SetRetentionOverrideAsync(metadataIds[activeInput], ManualOverrideDecision.Pin, playlist.ExternalId);

            await AcceptRetentionAsync(kept, playlist.PlaylistLinkId);

            var restored = await verify.Jobs.AsNoTracking().Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName)
                .ToArrayAsync();
            Assert.Equal(3 + transition, restored.Length);
            var pending = Assert.Single(restored, item => item.State == DurableJobState.Pending);
            Assert.Equal(restored.Length - 1, restored.Count(item => item.CancellationRequestedAt != null));
            var restoredPayload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(pending.PayloadJson)!;
            Assert.Equal(playlist.SourceSnapshotId, restoredPayload.SourceSnapshotId);
            Assert.Equal(playlist.ExternalId, restoredPayload.ExternalId);
            if (activeInput == 0) Assert.Equal(playlist.SourceEntryId, restoredPayload.SourceEntryId);
            else Assert.NotEqual(playlist.SourceEntryId, restoredPayload.SourceEntryId);
            Assert.InRange(pending.IdempotencyKey.Length, 1, 300);
        }
    }

    private async Task SetRetentionOverrideAsync(Guid metadataId, ManualOverrideDecision decision, string? target = null)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var metadata = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == metadataId);
        var previous = await db.ManualTrackOverrides.Where(item => item.OwnerUserId == owner && item.RevokedAt == null &&
            item.SourceProviderId == metadata.ProviderId && item.SourceExternalIdHash == metadata.ExternalIdHash).ToArrayAsync();
        foreach (var item in previous) item.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        db.ManualTrackOverrides.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ExternalSnapshotId = metadata.Id,
            SourceProviderId = metadata.ProviderId,
            SourceExternalIdHash = metadata.ExternalIdHash,
            TargetProviderId = target == null ? null : "deezer",
            TargetExternalId = target,
            Decision = decision,
            Reason = "Retention input change",
            DecisionVersion = previous.Select(item => item.DecisionVersion).DefaultIfEmpty(0).Max() + 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AcceptRetentionAsync(RecordingKeptFiles kept, Guid linkId)
    {
        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var db = new AllstarrDbContext(database.Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == linkId);
        await kept.StagePlaylistInterestsAsync(db, link, access, "stable-retention", default);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<Guid> PublishReorderedRetentionSnapshotAsync(PlaylistTrackRetentionJobPayload playlist,
        bool replaceFirst = false, bool freshMetadata = false)
    {
        await using var db = new AllstarrDbContext(database.Options);
        var source = await db.PlaylistSourceEntries.AsNoTracking().Where(item => item.PlaylistSourceSnapshotId == playlist.SourceSnapshotId)
            .OrderByDescending(item => item.SourcePosition).ToArrayAsync();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            PlaylistLinkId = playlist.PlaylistLinkId,
            OwnerUserId = owner,
            ProviderAccountId = account,
            SnapshotVersion = 2,
            ProviderRevision = "reordered-v2",
            Name = "Reordered",
            PayloadSha256 = PlaylistHash("reordered-v2"),
            CorrelationId = "stable-retention",
            RetrievedAt = now,
            PublishedAt = now
        };
        db.PlaylistSourceSnapshots.Add(snapshot);
        var entries = source.Where(item => !replaceFirst || item.SourcePosition != 0).Select((item, position) => new PlaylistSourceEntryRecord
        {
            Id = Guid.CreateVersion7(),
            PlaylistSourceSnapshotId = snapshot.Id,
            ExternalMetadataSnapshotId = item.ExternalMetadataSnapshotId,
            PublishedTrackMatchId = item.PublishedTrackMatchId,
            SourcePosition = position,
            SourceEntryIdHash = PlaylistHash($"reordered-{position}")
        }).ToList();
        if (freshMetadata)
        {
            var metadataIds = entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
            var previous = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => metadataIds.Contains(item.Id)).ToArrayAsync();
            var replacements = previous.ToDictionary(item => item.Id, item => new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = item.OwnerUserId,
                ProviderAccountId = item.ProviderAccountId,
                ProviderTrackIdentityId = item.ProviderTrackIdentityId,
                BackendInstanceId = item.BackendInstanceId,
                BackendPrincipalId = item.BackendPrincipalId,
                Protocol = item.Protocol,
                ProviderId = item.ProviderId,
                ResourceKind = item.ResourceKind,
                ExternalIdHash = item.ExternalIdHash,
                SnapshotVersion = item.SnapshotVersion + 1,
                ProviderRevision = "fresh-metadata-v2",
                PayloadJson = item.PayloadJson,
                PayloadSha256 = item.PayloadSha256,
                CorrelationId = "stable-retention",
                RetrievedAt = now
            });
            db.ExternalMetadataSnapshots.AddRange(replacements.Values);
            foreach (var entry in entries) entry.ExternalMetadataSnapshotId = replacements[entry.ExternalMetadataSnapshotId].Id;
        }
        if (replaceFirst)
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "source-track");
            var metadata = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderTrackIdentityId = identity.Id,
                BackendInstanceId = "fixture",
                BackendPrincipalId = owner.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = identity.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "added",
                PayloadJson = "{\"Title\":\"Added track\",\"Artists\":[\"Artist\"]}",
                PayloadSha256 = PlaylistHash("added-metadata"),
                CorrelationId = "stable-retention",
                RetrievedAt = now
            };
            db.ExternalMetadataSnapshots.Add(metadata);
            entries.Add(new()
            {
                Id = Guid.CreateVersion7(),
                PlaylistSourceSnapshotId = snapshot.Id,
                ExternalMetadataSnapshotId = metadata.Id,
                SourcePosition = entries.Count,
                SourceEntryIdHash = PlaylistHash("added-entry")
            });
        }
        db.PlaylistSourceEntries.AddRange(entries);
        await db.SaveChangesAsync();
        return snapshot.Id;
    }
}
