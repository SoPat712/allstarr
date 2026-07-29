using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class DurableProviderRouteSelectorTests
{
    [Fact]
    public void Select_UsesConfiguredPlayableVerifiedCanonicalRoutes()
    {
        var tenant = Guid.CreateVersion7();
        var canonical = Guid.CreateVersion7();
        var source = Identity(tenant, canonical, "spotify", "source");
        var qobuz = Identity(tenant, canonical, "qobuz", "qobuz");
        var deezer = Identity(tenant, canonical, "deezer", "deezer");
        deezer.Verification = ProviderIdentityVerification.Pinned;
        var unknown = Identity(tenant, canonical, "qobuz", "unknown");
        unknown.Verification = ProviderIdentityVerification.Unknown;
        var otherAccount = Identity(tenant, canonical, "qobuz", "other-account");
        otherAccount.Scope = ProviderIdentityScope.Account;
        otherAccount.ProviderAccountId = Guid.CreateVersion7();

        var routes = DurableProviderRouteSelector.Select(source,
        [
            source,
            deezer,
            qobuz,
            unknown,
            otherAccount,
            Identity(tenant, canonical, "tidal", "not-playable"),
            Identity(tenant, Guid.CreateVersion7(), "qobuz", "other-recording"),
            Identity(Guid.CreateVersion7(), canonical, "qobuz", "other-tenant")
        ], ["qobuz", "deezer", "tidal"]);

        Assert.Equal(["qobuz", "deezer"], routes.Select(item => item.ProviderId));
        Assert.False(routes[0].IsManual);
        Assert.True(routes[1].IsManual);
    }

    private static ProviderTrackIdentityRecord Identity(
        Guid tenant,
        Guid canonical,
        string provider,
        string externalId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            CanonicalRecordingId = canonical,
            ProviderId = provider,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = externalId,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "test",
            DecisionVersion = 1,
            VerifiedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
