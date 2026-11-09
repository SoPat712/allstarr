using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, true, false)]
    public async Task NativeAppleAliasUsesCanonicalSourceMetadataAndKeepsItsSourcePin(
        bool pinned, bool conflictingIsrc, bool linkedSnapshot, bool currentIdentity)
    {
        const string publicId = "ext-apple-musickit-song-1700522647";
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "playback", "USUM72308561", null, publicId: publicId);
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = recording.Recording.Id,
            ProviderId = "spotify",
            ExternalId = "source",
            ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
            ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = currentIdentity ? "fixture" : CatalogIdentityPolicy.SupersededVerificationMethod,
            DecisionVersion = 1
        };
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _userA,
            ProviderId = "spotify",
            ProviderTrackIdentityId = linkedSnapshot ? identity.Id : null,
            ResourceKind = "track",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "principal-a",
            ExternalIdHash = identity.ExternalIdHash,
            SnapshotVersion = 1,
            RetrievedAt = _clock.UtcNow,
            PayloadSha256 = new string('a', 64),
            PayloadJson = conflictingIsrc
                ? SelfLoveMatchFixture.Payload.Replace("\"IsExplicit\":false", "\"IsExplicit\":false,\"Isrc\":\"USAAA2600001\"")
                : SelfLoveMatchFixture.Payload
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            if (pinned) db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = "spotify",
                SourceExternalIdHash = snapshot.ExternalIdHash,
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "apple-musickit",
                TargetExternalId = "1700522647",
                DecisionVersion = 1,
                Revision = 5
            });
            await db.SaveChangesAsync();
        }

        var expected = pinned || conflictingIsrc || !currentIdentity ? null : SelfLoveMatchFixture.BackendItemId;
        var matches = await _service.FindKnownRecordingTracksAsync(viewer,
            [new(publicId, recording.Recording.Id, recording.Recording.Isrc, "apple-musickit", "1700522647")]);
        Assert.Equal(expected, matches.GetValueOrDefault(publicId));
        Assert.Equal(expected, await _service.ResolveKnownTrackAsync(viewer, publicId));
        await using var saved = _factory.CreateDbContext();
        Assert.Empty(await saved.TrackMatches.ToArrayAsync());
        if (pinned)
        {
            var pin = await saved.ManualTrackOverrides.SingleAsync();
            Assert.Equal("1700522647", pin.TargetExternalId);
            Assert.Equal(5, pin.Revision);
        }
    }

    [Fact]
    public async Task NativeLookupSeesALibraryChangeThroughItsSharedCopy()
    {
        const string publicId = "ext-apple-musickit-song-1700522647";
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "playback", "USUM72308561", null, publicId: publicId);
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = recording.Recording.Id,
            ProviderId = "spotify",
            ExternalId = "source",
            ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
            ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ProviderId = "spotify",
                ProviderTrackIdentityId = identity.Id,
                ResourceKind = "track",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal-a",
                ExternalIdHash = identity.ExternalIdHash,
                SnapshotVersion = 1,
                RetrievedAt = _clock.UtcNow,
                PayloadSha256 = new string('a', 64),
                PayloadJson = SelfLoveMatchFixture.Payload
            });
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            await db.SaveChangesAsync();
        }
        async Task<string?> Find() => (await _service.FindKnownRecordingTracksAsync(viewer,
            [new(publicId, recording.Recording.Id, recording.Recording.Isrc, "apple-musickit", "1700522647")])).GetValueOrDefault(publicId);

        Assert.Equal(SelfLoveMatchFixture.BackendItemId, await Find());
        Assert.Equal(SelfLoveMatchFixture.BackendItemId, await Find());
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Remove(await db.LibraryTracks.SingleAsync());
            await db.SaveChangesAsync();
        }
        Assert.Null(await Find());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("USAAA2600001", true)]
    [InlineData("USAAA2600002", false)]
    public async Task NativeCanonicalLookupChecksIsrcWithoutASourceSnapshot(string? isrc, bool matches)
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "playback", "USAAA2600001", null);
        var track = SelfLoveMatchFixture.Local(_userA, "backend");
        track.CanonicalRecordingId = recording.Recording.Id;
        track.Isrc = isrc;
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Add(track);
            await db.SaveChangesAsync();
        }

        var result = await _service.FindKnownRecordingTracksAsync(viewer,
            [new("ext-deezer-song-source", recording.Recording.Id, "USAAA2600001", "deezer", "source")]);
        Assert.Equal(matches ? SelfLoveMatchFixture.BackendItemId : null, result.GetValueOrDefault("ext-deezer-song-source"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSelfLoveLookupAgreesWithCanonicalProjectionAndPreservesProviderPins(bool pinned)
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "playback", null, null,
            publicId: "ext-deezer-song-source");
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _userA,
            ProviderId = "deezer",
            ResourceKind = "track",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "principal-a",
            ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
            SnapshotVersion = 1,
            PayloadSha256 = new string('a', 64),
            PayloadJson = SelfLoveMatchFixture.Payload,
            RetrievedAt = _clock.UtcNow
        };
        var track = SelfLoveMatchFixture.Local(_userA, "backend");
        await using (var db = _factory.CreateDbContext())
        {
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.LibraryTracks.Add(track);
            if (pinned) db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot.Id,
                SourceProviderId = "deezer",
                SourceExternalIdHash = snapshot.ExternalIdHash,
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "apple-musickit",
                TargetExternalId = "personal-choice",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }

        var matches = await _service.FindKnownRecordingTracksAsync(viewer,
            [new("ext-deezer-song-source", recording.Recording.Id, null, "deezer", "source")]);

        if (pinned) Assert.Empty(matches);
        else Assert.Equal(SelfLoveMatchFixture.BackendItemId, Assert.Single(matches).Value);
        Assert.Equal(pinned ? null : SelfLoveMatchFixture.BackendItemId,
            await _service.ResolveKnownTrackAsync(viewer, "ext-deezer-song-source"));
        await using var saved = _factory.CreateDbContext();
        Assert.Empty(await saved.TrackMatches.ToArrayAsync());
        if (pinned) Assert.Equal("personal-choice", (await saved.ManualTrackOverrides.SingleAsync()).TargetExternalId);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duration")]
    [InlineData("isrc")]
    public async Task NativeCanonicalLookupCannotRestoreAConflictingLibraryCopy(string conflict)
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "playback", null, null);
        var track = SelfLoveMatchFixture.Local(_userA, "backend");
        track.CanonicalRecordingId = recording.Recording.Id;
        var payload = SelfLoveMatchFixture.Payload;
        if (conflict == "version") track.Title += " (Live)";
        if (conflict == "duration") track.DurationMilliseconds = 240000;
        if (conflict == "isrc")
        {
            track.Isrc = "USAAA2600002";
            payload = payload.Replace("\"IsExplicit\":false", "\"IsExplicit\":false,\"Isrc\":\"USAAA2600001\"");
        }
        await using (var db = _factory.CreateDbContext())
        {
            db.LibraryTracks.Add(track);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _userA,
                ProviderId = "deezer",
                ResourceKind = "track",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal-a",
                ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
                SnapshotVersion = 1,
                PayloadSha256 = new string('a', 64),
                PayloadJson = payload,
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _service.FindKnownRecordingTracksAsync(viewer,
            [new("ext-deezer-song-source", recording.Recording.Id, null, "deezer", "source")]));
    }
}
