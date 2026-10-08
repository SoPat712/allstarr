using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class TrackClassifierTests
{
    [Fact]
    public void Classify_UsesOneThresholdOverrideAndFallbackPolicy()
    {
        var tenant = Guid.CreateVersion7();
        var canonical = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        var source = Identity(tenant, canonical, "spotify", "source");
        var fallback = Identity(tenant, canonical, "qobuz", "fallback");
        var suggested = Decision(local, TrackMatchState.Suggested, .87, .88);

        var classification = TrackClassifier.Classify(
            null, suggested, source, [source, fallback], ["qobuz"], new HashSet<Guid> { local });

        Assert.Equal(TrackMatchState.Suggested, classification.State);
        Assert.Equal(local, classification.LibraryTrackId);
        Assert.Equal(TrackRouteKind.Local, classification.RouteKind);

        var pinned = TrackClassifier.Classify(
            new ManualTrackOverrideRecord
            {
                Decision = ManualOverrideDecision.Pin,
                LibraryTrackId = local
            },
            suggested,
            source,
            [source, fallback],
            ["qobuz"],
            new HashSet<Guid> { local });
        Assert.Equal(TrackMatchState.Pinned, pinned.State);
        Assert.Equal(local, pinned.LibraryTrackId);
        Assert.Equal(TrackRouteKind.Local, pinned.RouteKind);

        var rejected = TrackClassifier.Classify(
            new ManualTrackOverrideRecord { Decision = ManualOverrideDecision.Reject },
            suggested,
            source,
            [source, fallback],
            ["qobuz"],
            new HashSet<Guid> { local });
        Assert.Equal(TrackMatchState.Rejected, rejected.State);
        Assert.Equal(TrackRouteKind.Unresolved, rejected.RouteKind);

        var rejectedCandidate = TrackClassifier.Classify(
            new ManualTrackOverrideRecord
            {
                Decision = ManualOverrideDecision.Reject,
                LibraryTrackId = local,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion
            },
            suggested,
            source,
            [source, fallback],
            ["qobuz"],
            new HashSet<Guid> { local });
        Assert.Equal(TrackMatchState.Rejected, rejectedCandidate.State);
        Assert.Equal(TrackRouteKind.External, rejectedCandidate.RouteKind);
    }

    [Fact]
    public void Classify_DoesNotPromoteBelowThresholdOrUnavailableLocalCandidate()
    {
        var local = Guid.CreateVersion7();
        var belowThreshold = TrackClassifier.Classify(
            null,
            Decision(local, TrackMatchState.Accepted, .7, .88),
            playableLibraryTrackIds: new HashSet<Guid> { local });
        Assert.Equal(TrackMatchState.Unresolved, belowThreshold.State);
        Assert.Equal(TrackRouteKind.Unresolved, belowThreshold.RouteKind);

        var unavailable = TrackClassifier.Classify(
            null,
            Decision(local, TrackMatchState.Accepted, .9, .88),
            playableLibraryTrackIds: new HashSet<Guid>());
        Assert.Equal(TrackMatchState.Accepted, unavailable.State);
        Assert.Null(unavailable.LibraryTrackId);
    }

    [Fact]
    public void Classify_CatalogPinsDoNotBecomePersonalAuthority()
    {
        var tenant = Guid.CreateVersion7();
        var canonical = Guid.CreateVersion7();
        var source = Identity(tenant, canonical, "spotify", "source");
        var pinned = Identity(tenant, canonical, "apple-download", "target");
        pinned.Verification = ProviderIdentityVerification.Pinned;
        var ambiguous = Decision(Guid.Empty, TrackMatchState.Ambiguous, .958, .88);
        ambiguous.LibraryTrackId = null;

        var accepted = TrackClassifier.Classify(
            null, ambiguous, source, [source, pinned], ["apple-download"]);
        var rejected = TrackClassifier.Classify(
            new ManualTrackOverrideRecord { Decision = ManualOverrideDecision.Reject },
            ambiguous, source, [source, pinned], ["apple-download"]);

        Assert.Equal(TrackRouteKind.External, accepted.RouteKind);
        Assert.Equal(TrackMatchState.Ambiguous, accepted.ReviewState);
        Assert.False(accepted.PrimaryProviderRoute!.IsManual);
        Assert.Equal(TrackMatchState.Rejected, rejected.ReviewState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderPin_OverridesAutomaticLocalOnlyWhenViewerCanUseProvider(bool eligible)
    {
        var tenant = Guid.CreateVersion7();
        var canonical = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        var source = Identity(tenant, canonical, "spotify", "source");
        var snapshot = new ExternalMetadataSnapshotRecord { TenantId = tenant, BackendInstanceId = "backend" };
        var track = new LibraryTrackRecord
        {
            Id = local,
            TenantId = tenant,
            BackendInstanceId = "backend",
            CanonicalRecordingId = canonical,
            LibraryScopeId = "music",
            BackendItemId = "native"
        };
        var decision = Decision(local, TrackMatchState.Accepted, 1, .88);
        decision.CanonicalRecordingId = canonical;
        var pin = new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = "deezer",
            TargetExternalId = "personal-choice"
        };
        var projection = TrackRouteProjector.Project(snapshot, decision, pin, source, [track], [source],
            eligible ? ["deezer"] : []);
        Assert.Equal(eligible ? TrackMatchState.Pinned : TrackMatchState.Accepted, projection.ReviewState);
        if (eligible)
        {
            Assert.Null(projection.LibraryTrack);
            Assert.True(projection.PrimaryProviderRoute!.IsManual);
            Assert.Equal("personal-choice", projection.PrimaryProviderRoute.ExternalId);
        }
        else
        {
            Assert.Equal(local, projection.LibraryTrack!.Id);
            Assert.Null(projection.PrimaryProviderRoute);
        }
    }

    private static TrackMatchRecord Decision(
        Guid local,
        TrackMatchState state,
        double confidence,
        double threshold) => new()
        {
            State = state,
            LibraryTrackId = local,
            Confidence = confidence,
            Threshold = threshold
        };

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
            VerificationMethod = "test"
        };
}
