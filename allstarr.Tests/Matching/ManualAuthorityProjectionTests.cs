using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ManualAuthorityProjectionTests
{
    [Fact]
    public void MappingRow_ExposesProviderAndRejectionAuthoritiesWithConcurrencyRevisions()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        var authoritySnapshotId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var source = Identity(tenantId, canonicalId, "spotify", "source", false);
        var provider = Identity(tenantId, canonicalId, "qobuz", "target", true);
        provider.Revision = 4;
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = snapshotId,
            TenantId = tenantId,
            OwnerUserId = ownerId,
            ProviderTrackIdentityId = source.Id,
            ProviderId = "spotify",
            LibraryScopeId = "music",
            BackendInstanceId = "backend",
            PayloadJson = """{"title":"Track","artist":"Artist"}"""
        };
        var decision = new TrackMatchRecord
        {
            ExternalSnapshotId = authoritySnapshotId,
            CanonicalRecordingId = canonicalId,
            State = TrackMatchState.Accepted,
            Confidence = 1,
            Threshold = .88,
            CandidateResultsJson = "[]"
        };
        var providerRow = Row(snapshot, decision, null, source, [source, provider]);
        var providerAuthority = Assert.Single(providerRow.GetProperty("manualAuthorities").EnumerateArray());
        Assert.Equal("provider_match", providerAuthority.GetProperty("kind").GetString());
        Assert.Equal(provider.Id, providerAuthority.GetProperty("id").GetGuid());
        Assert.Equal(4, providerAuthority.GetProperty("revision").GetInt64());
        Assert.Equal(
            authoritySnapshotId,
            providerAuthority.GetProperty("authoritySnapshotId").GetGuid());

        var rejectionId = Guid.CreateVersion7();
        var rejected = new ManualTrackOverrideRecord
        {
            Id = rejectionId,
            ExternalSnapshotId = snapshotId,
            Decision = ManualOverrideDecision.Reject,
            Reason = "Wrong result",
            MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Revision = 2
        };
        var rejectionRow = Row(snapshot, decision, rejected, source, [source]);
        var rejection = Assert.Single(rejectionRow.GetProperty("manualAuthorities").EnumerateArray());
        Assert.Equal("rejection", rejection.GetProperty("kind").GetString());
        Assert.Equal(rejectionId, rejection.GetProperty("id").GetGuid());
        Assert.Equal(2, rejection.GetProperty("revision").GetInt64());
    }

    private static JsonElement Row(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord decision,
        ManualTrackOverrideRecord? manual,
        ProviderTrackIdentityRecord source,
        ProviderTrackIdentityRecord[] identities)
    {
        var row = typeof(TrackMatchesController).GetMethod(
            "Row",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
        [
            snapshot,
            decision,
            manual,
            source,
            new Dictionary<Guid, LibraryTrackRecord>(),
            new Dictionary<Guid, ProviderTrackIdentityRecord[]> { [source.CanonicalRecordingId] = identities },
            new HashSet<string>(["qobuz", "spotify"])
        ])!;
        var value = row.GetType().GetProperty("Value")!.GetValue(row);
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static ProviderTrackIdentityRecord Identity(
        Guid tenantId,
        Guid canonicalId,
        string providerId,
        string externalId,
        bool manual) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CanonicalRecordingId = canonicalId,
            ProviderId = providerId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = new string(providerId == "spotify" ? 'a' : 'b', 64),
            Verification = manual ? ProviderIdentityVerification.Pinned : ProviderIdentityVerification.Verified,
            VerificationMethod = manual
            ? ManualTrackAuthorityPolicy.ProviderVerificationMethod
            : "source-snapshot",
            DecisionVersion = 1,
            VerifiedAt = DateTimeOffset.UtcNow
        };
}
