using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ActivityMatchProjectionTests
{
    [Fact]
    public void AcceptedProviderDecision_ProjectsThePlayableCanonicalRoute()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var source = Identity(tenantId, canonicalId, "spotify", "source-track");
        var target = Identity(tenantId, canonicalId, "qobuz", "target-track");
        var lowerPriority = Identity(tenantId, canonicalId, "deezer", "other-track");
        var snapshot = Snapshot(tenantId, ownerId, source.Id);
        var decision = Decision(snapshot.Id, canonicalId, JsonSerializer.Serialize(new[]
        {
            new TrackMatchCandidateScore(
                Guid.CreateVersion7(),
                "external-qobuz-target-track",
                1,
                ["title_exact", "artist_exact"],
                [],
                Title: "Enough Now",
                Artist: "Emerson Azarian",
                Album: "Enough Now",
                ProviderTrackIds: new Dictionary<string, string> { ["qobuz"] = "target-track" },
                IsLocal: false)
        }));

        var activity = AdminUiController.MatchActivityItem(
            decision,
            snapshot,
            source,
            [source, target, lowerPriority],
            [],
            ["qobuz", "deezer"]);

        Assert.Equal("qobuz", activity.TargetProviderId);
        Assert.Equal("target-track", activity.TargetProviderTrackId);
        Assert.Equal("Enough Now", activity.TargetTitle);
        Assert.Equal("Emerson Azarian", activity.TargetArtist);
        Assert.DoesNotContain("no playable match", activity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedCanonicalDecision_ProjectsTheScopedLocalFallback()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var source = Identity(tenantId, canonicalId, "spotify", "source-track");
        var snapshot = Snapshot(tenantId, ownerId, source.Id);
        var decision = Decision(snapshot.Id, canonicalId, "[]");
        var local = new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerId,
            CanonicalRecordingId = canonicalId,
            BackendInstanceId = snapshot.BackendInstanceId,
            LibraryScopeId = snapshot.LibraryScopeId,
            BackendItemId = "local-track",
            Title = "Enough Now",
            Artist = "Emerson Azarian"
        };

        var activity = AdminUiController.MatchActivityItem(
            decision,
            snapshot,
            source,
            [source],
            [local]);

        Assert.Equal("library", activity.TargetProviderId);
        Assert.Equal("local-track", activity.BackendItemId);
        Assert.Equal("Enough Now", activity.TargetTitle);
    }

    private static ProviderTrackIdentityRecord Identity(
        Guid tenantId,
        Guid canonicalId,
        string providerId,
        string externalId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CanonicalRecordingId = canonicalId,
            ProviderId = providerId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = new string('a', 64),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "automatic-match",
            DecisionVersion = 1,
            VerifiedAt = DateTimeOffset.UtcNow
        };

    private static ExternalMetadataSnapshotRecord Snapshot(
        Guid tenantId,
        Guid ownerId,
        Guid sourceIdentityId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerId,
            ProviderTrackIdentityId = sourceIdentityId,
            ProviderId = "spotify",
            BackendInstanceId = "server",
            LibraryScopeId = "music",
            PayloadJson = """{"title":"Enough Now","artist":"Emerson Azarian","album":"Enough Now"}"""
        };

    private static TrackMatchRecord Decision(
        Guid snapshotId,
        Guid canonicalId,
        string candidates) => new()
        {
            Id = Guid.CreateVersion7(),
            ExternalSnapshotId = snapshotId,
            CanonicalRecordingId = canonicalId,
            State = TrackMatchState.Accepted,
            Confidence = 1,
            Threshold = 0.88,
            DecisionVersion = 1,
            PolicyVersion = "test",
            CandidateResultsJson = candidates,
            DecidedAt = DateTimeOffset.UtcNow
        };
}
