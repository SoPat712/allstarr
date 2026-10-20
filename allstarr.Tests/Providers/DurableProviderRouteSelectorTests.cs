using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using System.Collections.Immutable;

namespace allstarr.Tests;

public sealed class DurableProviderRouteSelectorTests
{
    [Fact]
    public void ManualOnlyIdentityRequiresAnExplicitPinAndOffPreservesNormalAlternatives()
    {
        var recording = Guid.CreateVersion7();
        var source = Identity(recording, "spotify", "source");
        var manualIdentity = Identity(recording, "manual", "demo-v3");
        var normal = Identity(recording, "deezer", "released");
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", 0)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("manual", ProviderUseMode.ManualOnly) };
        var order = new[] { "manual", "deezer" };
        Assert.Equal("deezer", Assert.Single(DurableProviderRouteSelector.Select(source,
            [manualIdentity, normal], order, providerPolicy: policy)).ProviderId);
        var pin = new ManualTrackOverrideRecord
        { Decision = ManualOverrideDecision.Pin, TargetProviderId = "manual", TargetExternalId = "demo-v3" };
        var pinned = DurableProviderRouteSelector.Select(source, [manualIdentity, normal], order, pin, providerPolicy: policy);
        Assert.True(pinned[0].IsManual);
        Assert.Equal("demo-v3", pinned[0].ExternalId);
        var off = policy with { ProviderModes = policy.ProviderModes.SetItem("manual", ProviderUseMode.Off) };
        Assert.Equal("deezer", Assert.Single(DurableProviderRouteSelector.Select(source,
            [manualIdentity, normal], order, pin, providerPolicy: off)).ProviderId);
        Assert.Empty(DurableProviderRouteSelector.Select(null, [], ["manual"], pin, providerPolicy: off));
    }
    [Theory]
    [InlineData("apple-download")]
    [InlineData("applemusic")]
    [InlineData("apple-musickit")]
    public void Select_RecognizesAppleCatalogAliasesInStreamingPriority(string provider)
    {
        var canonical = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var apple = Identity(canonical, provider, "apple-track");
        var deezer = Identity(canonical, "deezer", "deezer-track");

        var routes = DurableProviderRouteSelector.Select(
            source, [deezer, apple], ["apple-musickit", "deezer"]);

        Assert.Equal(["apple-track", "deezer-track"], routes.Select(item => item.ExternalId));
        var manual = new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = provider,
            TargetExternalId = "manual-apple"
        };
        routes = DurableProviderRouteSelector.Select(
            source, [deezer, apple], ["apple-musickit", "deezer"], manual);
        Assert.True(routes[0].IsManual);
        Assert.Equal("manual-apple", routes[0].ExternalId);
    }

    [Fact]
    public void Select_DeduplicatesAliasesWithoutDuplicatingAManualRoute()
    {
        var canonical = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var manual = new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = "applemusic",
            TargetExternalId = "apple-track"
        };
        var identities = new[]
        {
            Identity(canonical, "apple-download", "apple-track"),
            Identity(canonical, "apple-musickit", "apple-track"),
            Identity(canonical, "deezer", "deezer-track")
        };
        var order = new[] { "apple-musickit", "apple-download", "deezer" };

        var automatic = DurableProviderRouteSelector.Select(source, identities, order);
        Assert.Equal(["apple-track", "deezer-track"], automatic.Select(item => item.ExternalId));
        var routes = DurableProviderRouteSelector.Select(source, identities, order, manual);
        Assert.Equal(["apple-track", "deezer-track"], routes.Select(item => item.ExternalId));
        Assert.True(routes[0].IsManual);
    }

    [Fact]
    public void Select_UsesConfiguredPlayableVerifiedCanonicalRoutes()
    {
        var canonical = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var qobuz = Identity(canonical, "qobuz", "qobuz");
        var deezer = Identity(canonical, "deezer", "deezer");
        deezer.Verification = ProviderIdentityVerification.Pinned;
        var unknown = Identity(canonical, "qobuz", "unknown");
        unknown.Verification = ProviderIdentityVerification.Unknown;
        var otherAccount = Identity(canonical, "qobuz", "other-account");
        otherAccount.Scope = ProviderIdentityScope.Account;
        otherAccount.ProviderAccountId = Guid.CreateVersion7();

        var routes = DurableProviderRouteSelector.Select(source,
        [
            source,
            deezer,
            qobuz,
            unknown,
            otherAccount,
            Identity(canonical, "tidal", "not-playable"),
            Identity(Guid.CreateVersion7(), "qobuz", "other-recording"),
            new ProviderTrackIdentityRecord { CanonicalRecordingId = canonical, ProviderId = "qobuz", ResourceKind = ProviderResourceKind.Album, ExternalId = "wrong-kind", Verification = ProviderIdentityVerification.Verified }
        ], ["qobuz", "deezer", "tidal"]);

        Assert.Equal(["qobuz", "deezer"], routes.Select(item => item.ProviderId));
        Assert.False(routes[0].IsManual);
        Assert.False(routes[1].IsManual);
    }

    private static ProviderTrackIdentityRecord Identity(
        Guid canonical,
        string provider,
        string externalId) => new()
        {
            Id = Guid.CreateVersion7(),
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
