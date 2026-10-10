using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SourceIdentityReconciliationTests
{
    [Theory]
    [InlineData("provisional", false, true)]
    [InlineData("provisional", true, true)]
    [InlineData("raw", false, true)]
    [InlineData("isrc", false, false)]
    [InlineData("mbid", false, false)]
    [InlineData("confirmed", false, false)]
    [InlineData("manual", false, false)]
    public async Task Reconciliation_IsAtomicIdempotentAndProtectsStrongEvidence(
        string evidence, bool alreadyMoved, bool expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var seed = await SeedAsync(db, evidence, alreadyMoved);
        var before = seed.Identity.CanonicalRecordingId;
        var now = DateTimeOffset.UtcNow;
        var result = await TrackMatchCommandService.TryRetargetProvisionalIdentityAsync(
            db, seed.Identity, seed.Target, now, default);
        Assert.Equal(expected, result);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var identity = await db.ProviderTrackIdentities.SingleAsync();
        Assert.Equal(expected ? seed.Target : before, identity.CanonicalRecordingId);
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync());
        if (!expected) return;

        var revision = identity.Revision;
        Assert.True(await TrackMatchCommandService.TryRetargetProvisionalIdentityAsync(
            db, identity, seed.Target, now, default));
        await db.SaveChangesAsync();
        Assert.Equal(revision, identity.Revision);
    }

    [Fact]
    public async Task Reconciliation_LeavesUnknownUserIdentityUntouched()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var seed = await SeedAsync(db, "provisional", false);
        Assert.Equal(seed.Origin, seed.Identity.CanonicalRecordingId);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    private static async Task<(ProviderActorContext Actor, ProviderTrackIdentityRecord Identity, Guid Origin, Guid Target)>
        SeedAsync(AllstarrDbContext db, string evidence, bool alreadyMoved)
    {
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var origin = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        db.Users.Add(new UserRecord
        {
            Id = user,
            DisplayName = "Owner",
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "owner",
            CreatedAt = now,
            UpdatedAt = now
        });
        var originRecording = new CanonicalRecordingRecord
        {
            Id = origin,
            CreatedByUserId = user,
            IsProvisional = evidence != "confirmed",
            CreatedAt = now,
            UpdatedAt = now
        };
        if (evidence == "isrc")
        {
            originRecording.Identifiers.Add(new RecordingIdentifierRecord
            {
                Id = Guid.CreateVersion7(),
                RecordingId = origin,
                Kind = RecordingIdentifierKinds.Isrc,
                Value = "USRC17607839",
                Source = "fixture",
                CreatedAt = now
            });
        }
        else if (evidence == "mbid")
        {
            originRecording.Identifiers.Add(new RecordingIdentifierRecord
            {
                Id = Guid.CreateVersion7(),
                RecordingId = origin,
                Kind = RecordingIdentifierKinds.MusicBrainz,
                Value = Guid.CreateVersion7().ToString("D"),
                Source = "fixture",
                CreatedAt = now
            });
        }

        db.CanonicalRecordings.AddRange(originRecording, new CanonicalRecordingRecord
        {
            Id = target,
            CreatedByUserId = user,
            CreatedAt = now,
            UpdatedAt = now
        });
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = alreadyMoved ? target : origin,
            ProviderId = "fixture",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = evidence == "raw" ? "source" : CanonicalCatalogKeys.Hash("source"),
            ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
            Verification = evidence == "manual" ? ProviderIdentityVerification.Pinned : ProviderIdentityVerification.Verified,
            VerificationMethod = evidence switch
            {
                "manual" => "manual-review",
                "raw" => "source-snapshot",
                _ => "source-snapshot-hash"
            },
            DecisionVersion = 1,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProviderTrackIdentities.Add(identity);
        await db.SaveChangesAsync();
        return (new ProviderActorContext(ProviderActorKind.User, user,
            new ProviderBackendPrincipal("jellyfin", "backend", "owner")), identity, origin, target);
    }
}
