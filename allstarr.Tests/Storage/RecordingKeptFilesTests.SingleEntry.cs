using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RetentionEntryReaderMaterializesOnlyTheRequestedEntryFromA10000TrackSource()
    {
        var payload = await SeedLargeRetentionPlaylistAsync();
        var probe = new RetentionMaterializationProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), probe)
            .Options;
        var boundedFactory = new Factory(options);
        var access = await new TestBackendLibraryAccess(boundedFactory, "music").ResolveUserAsync(owner);
        await using var db = new AllstarrDbContext(options);
        var reader = new DurablePlaylistProjectionReader(
            boundedFactory, new TestBackendLibraryAccess(boundedFactory, "music"));

        var result = await reader.ReadRetentionEntryAsync(
            db, owner, payload.PlaylistLinkId, payload.SourceSnapshotId, payload.SourceEntryId,
            access, default);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Position);
        Assert.Equal("large-track-00000", result.ExternalId);
        Assert.Equal(1, probe.SourceEntryRows);
        Assert.Equal(1, probe.MetadataRows);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task RetentionWorkerMaterializesOnlyItsTrackFromA10000TrackPlaylist()
    {
        var payload = await SeedLargeRetentionPlaylistAsync();
        var probe = new RetentionMaterializationProbe();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), probe).Options;
        factory = new Factory(options);
        await services.DisposeAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<AllstarrDbContext>(builder => builder.UseSqlite(database.ConnectionString)
            .AddInterceptors(new SqlitePragmaInterceptor(), probe));
        collection.AddManagedFilePlacement();
        services = collection.BuildServiceProvider();
        await using (var db = new AllstarrDbContext(options))
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
            job.Type = PlaylistTrackRetentionJobHandler.JobTypeName;
            job.PayloadJson = JsonSerializer.Serialize(payload);
            job.Revision++;
            await db.SaveChangesAsync();
        }
        var claim = new DurableJobClaim(jobId, 1, PlaylistTrackRetentionJobHandler.JobTypeName,
            JsonSerializer.SerializeToElement(payload), owner, null, null,
            JsonSerializer.SerializeToElement(new { }), "large-retention-worker");

        Assert.Null(await PlaylistService().RetainPlaylistAsync(claim));

        Assert.InRange(probe.SourceEntryRows, 1, 2);
        Assert.Equal(1, probe.MetadataRows);
    }

    [Fact]
    public async Task RetentionReaderPreservesDuplicateEntriesSharingAnOwnedMetadataSnapshot()
    {
        var payload = await SeedLargeRetentionPlaylistAsync(2, sharedMetadata: true);
        Guid[] entryIds;
        await using (var db = new AllstarrDbContext(database.Options))
            entryIds = await db.PlaylistSourceEntries.AsNoTracking().Where(item =>
                item.PlaylistSourceSnapshotId == payload.SourceSnapshotId).OrderBy(item => item.SourcePosition)
                .Select(item => item.Id).ToArrayAsync();
        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var verify = new AllstarrDbContext(database.Options);
        var reader = new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music"));

        var entriesById = await reader.ReadRetentionEntriesAsync(verify, owner, payload.PlaylistLinkId,
            payload.SourceSnapshotId, entryIds, access, default);

        Assert.Equal(2, entriesById.Count);
        Assert.Equal(new[] { 0, 1 }, entriesById.Values.Select(item => item.Position).Order());
        Assert.All(entriesById.Values, entry => Assert.Equal("large-track-00000", entry.ExternalId));
    }

    [Fact]
    public async Task RetentionEntryReaderRejectsAnExpectedSnapshotThatIsNoLongerLatest()
    {
        var payload = await SeedLargeRetentionPlaylistAsync(2);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = owner,
                PlaylistLinkId = payload.PlaylistLinkId,
                ProviderAccountId = account,
                SnapshotVersion = 2,
                ProviderRevision = "large-v2",
                Name = "Large playlist",
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData("large-v2"u8.ToArray())),
                CorrelationId = "single-entry",
                RetrievedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1)
            });
            await db.SaveChangesAsync();
        }

        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var verify = new AllstarrDbContext(database.Options);
        var reader = new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music"));

        Assert.Null(await reader.ReadRetentionEntryAsync(
            verify, owner, payload.PlaylistLinkId, payload.SourceSnapshotId, payload.SourceEntryId,
            access, default));
    }

    [Fact]
    public async Task RetentionEntryReaderUsesTheEffectiveLocalRoute()
    {
        var payload = await SeedPlaylistInterestAsync();
        var localId = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = localId,
                OwnerUserId = owner,
                CanonicalRecordingId = recordingId,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendItemId = "local-retention-item",
                FilePath = "/fixture/local-retention-item.flac",
                Title = "Fixture",
                Artist = "Artist",
                DurationMilliseconds = 180_000,
                IndexedAt = DateTimeOffset.UtcNow,
                SourceModifiedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            (await db.TrackMatches.SingleAsync()).LibraryTrackId = localId;
            await db.SaveChangesAsync();
        }

        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var verify = new AllstarrDbContext(database.Options);
        var reader = new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music"));
        var result = await reader.ReadRetentionEntryAsync(
            verify, owner, payload.PlaylistLinkId, payload.SourceSnapshotId, payload.SourceEntryId,
            access, default);

        Assert.NotNull(result);
        Assert.Equal("local", result!.RouteKind);
        Assert.Equal("local-retention-item", result.BackendItemId!);
    }

    [Fact]
    public async Task RetentionEntryReaderUsesTheEffectiveManualRoute()
    {
        var payload = await SeedPlaylistInterestAsync();
        await ChangePlaylistAsync(payload, "manual-provider");
        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var db = new AllstarrDbContext(database.Options);
        var reader = new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music"));

        var result = await reader.ReadRetentionEntryAsync(
            db, owner, payload.PlaylistLinkId, payload.SourceSnapshotId, payload.SourceEntryId,
            access, default);

        Assert.NotNull(result);
        Assert.Equal("external", result!.RouteKind);
        Assert.Equal("deezer", result.RouteProviderId!);
        Assert.Equal("different-track", result.ExternalId);
    }

    [Fact]
    public async Task RetentionEntryReaderUsesTheMatchedCanonicalForProviderInputs()
    {
        var payload = await SeedPlaylistInterestAsync();
        var matchedCanonicalId = Guid.CreateVersion7();
        var matchedIdentityId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var external = await db.ExternalMetadataSnapshots.SingleAsync();
            var match = await db.TrackMatches.SingleAsync();
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = matchedCanonicalId,
                CreatedByUserId = owner,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(matchedCanonicalId),
                Title = "Matched canonical",
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
            {
                Id = matchedIdentityId,
                CanonicalRecordingId = matchedCanonicalId,
                ProviderId = "qobuz",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = "matched-external",
                ExternalIdHash = PlaylistHash("matched-external"),
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "fixture-match",
                DecisionVersion = 1,
                VerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            match.CanonicalRecordingId = matchedCanonicalId;
            match.LibraryTrackId = null;
            match.State = TrackMatchState.Accepted;
            match.Revision++;
            await db.SaveChangesAsync();
        }

        var access = await new TestBackendLibraryAccess(factory, "music").ResolveUserAsync(owner);
        await using var verify = new AllstarrDbContext(database.Options);
        var reader = new DurablePlaylistProjectionReader(factory, new TestBackendLibraryAccess(factory, "music"));
        var result = await reader.ReadRetentionEntryAsync(
            verify, owner, payload.PlaylistLinkId, payload.SourceSnapshotId, payload.SourceEntryId,
            access, default);

        Assert.NotNull(result);
        Assert.Equal(matchedCanonicalId, result!.CanonicalRecordingId!.Value);
        Assert.Equal("external", result.RouteKind);
        Assert.Equal("qobuz", result.RouteProviderId!);
        Assert.Equal("matched-external", result.ExternalId);
    }

    private async Task<PlaylistTrackRetentionJobPayload> SeedLargeRetentionPlaylistAsync(int count = 10_000, bool sharedMetadata = false)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var now = DateTimeOffset.UtcNow;
        var link = new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            ProviderAccountId = account,
            SourceProviderId = "deezer",
            SourcePlaylistId = "large-playlist",
            SourcePlaylistIdHash = PlaylistHash("large-playlist"),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "fixture",
            TrackRetention = PlaylistTrackRetention.KeepAll,
            RuleVersion = "single-entry",
            PolicyVersion = "single-entry",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            ProviderAccountId = account,
            SnapshotVersion = 1,
            ProviderRevision = "large-v1",
            Name = "Large playlist",
            PayloadSha256 = PlaylistHash("large-snapshot"),
            CorrelationId = "single-entry",
            RetrievedAt = now,
            PublishedAt = now
        };
        var canonicals = new List<CanonicalRecordingRecord>(count);
        var identities = new List<ProviderTrackIdentityRecord>(count);
        var metadata = new List<ExternalMetadataSnapshotRecord>(count);
        var entries = new List<PlaylistSourceEntryRecord>(count);
        for (var position = 0; position < count; position++)
        {
            var externalId = $"large-track-{position:D5}";
            var externalHash = PlaylistHash(externalId);
            var canonicalId = Guid.CreateVersion7();
            var identityId = Guid.CreateVersion7();
            var metadataId = Guid.CreateVersion7();
            var entryId = Guid.CreateVersion7();
            canonicals.Add(new()
            {
                Id = canonicalId,
                CreatedByUserId = owner,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(canonicalId),
                Title = $"Large {position}",
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            identities.Add(new()
            {
                Id = identityId,
                CanonicalRecordingId = canonicalId,
                ProviderAccountId = account,
                ProviderId = "deezer",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Account,
                ExternalId = externalId,
                ExternalIdHash = externalHash,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "single-entry",
                DecisionVersion = 1,
                VerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            metadata.Add(new()
            {
                Id = metadataId,
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderTrackIdentityId = identityId,
                BackendInstanceId = "fixture",
                BackendPrincipalId = owner.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = externalHash,
                SnapshotVersion = 1,
                ProviderRevision = "large-v1",
                PayloadJson = $"{{\"Title\":\"Large {position}\",\"Artists\":[\"Artist {position}\"]}}",
                PayloadSha256 = PlaylistHash($"metadata-{position}"),
                CorrelationId = "single-entry",
                RetrievedAt = now
            });
            entries.Add(new()
            {
                Id = entryId,
                PlaylistSourceSnapshotId = snapshot.Id,
                ExternalMetadataSnapshotId = sharedMetadata && position > 0 ? metadata[0].Id : metadataId,
                SourcePosition = position,
                SourceEntryIdHash = PlaylistHash($"entry-{position}")
            });
        }
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.PlaylistLinks.Add(link);
            db.PlaylistSourceSnapshots.Add(snapshot);
            db.CanonicalRecordings.AddRange(canonicals);
            db.ProviderTrackIdentities.AddRange(identities);
            db.ExternalMetadataSnapshots.AddRange(metadata);
            db.PlaylistSourceEntries.AddRange(entries);
            await db.SaveChangesAsync();
        }
        return new(link.Id, snapshot.Id, entries[0].Id, "deezer", "large-track-00000",
            PlaylistHash($"{link.Id:N}\u001fdeezer\u001flarge-track-00000"), "Large 0", ["Artist 0"], null);
    }

    private sealed class RetentionMaterializationProbe : IMaterializationInterceptor
    {
        public int SourceEntryRows { get; private set; }
        public int MetadataRows { get; private set; }

        public InterceptionResult<object> CreatingInstance(
            MaterializationInterceptionData materializationData,
            InterceptionResult<object> result) => result;

        public object CreatedInstance(MaterializationInterceptionData materializationData, object entity) => entity;

        public InterceptionResult InitializingInstance(
            MaterializationInterceptionData materializationData,
            object entity,
            InterceptionResult result) => result;

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is PlaylistSourceEntryRecord) SourceEntryRows++;
            if (entity is ExternalMetadataSnapshotRecord) MetadataRows++;
            return entity;
        }
    }
}
