using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using System.Collections.Immutable;

namespace allstarr.Tests;

public sealed class TrackClassifierTests
{
    [Fact]
    public void QualifiedLibraryProjectionDoesNotRequireAStreamingFallback()
    {
        var owner = Guid.NewGuid();
        var source = Identity(Guid.NewGuid(), "spotify", "source");
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BackendInstanceId = "backend",
            ProviderId = "spotify",
            SnapshotVersion = 1,
            PayloadJson = SelfLoveMatchFixture.Payload
        };
        var track = SelfLoveMatchFixture.Local(owner, "backend");

        var projection = TrackRouteProjector.Project(snapshot, null, null, source, [track], [source], []);

        Assert.Equal(TrackMatchState.Accepted, projection.ReviewState);
        Assert.Equal(track.Id, projection.LibraryTrack?.Id);
        Assert.Empty(projection.Classification.ProviderRoutes);
    }

    [Fact]
    public void UnavailableStoredPinCannotBeReplacedByAnotherCanonicalLibraryCopy()
    {
        var owner = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var source = Identity(canonical, "spotify", "source");
        var other = SelfLoveMatchFixture.Local(owner, "backend");
        other.CanonicalRecordingId = canonical;
        var decision = Decision(Guid.NewGuid(), TrackMatchState.Pinned, 1, .88);
        decision.CanonicalRecordingId = canonical;

        var projection = TrackRouteProjector.Project(new ExternalMetadataSnapshotRecord
        { OwnerUserId = owner, BackendInstanceId = "backend" }, decision, null, source, [other], [source], []);

        Assert.Null(projection.LibraryTrack);
        Assert.Equal(TrackMatchState.Pinned, decision.State);
        Assert.NotEqual(other.Id, decision.LibraryTrackId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("version")]
    [InlineData("duration")]
    [InlineData("isrc")]
    public void SelfLoveCanonicalProjectionRequiresQualifiedLibraryEvidence(string? conflict)
    {
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        var source = Identity(recording, "spotify", "source");
        var apple = Identity(recording, "apple-musickit", "1700522647");
        var deezer = Identity(recording, "deezer", "fallback");
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BackendInstanceId = "backend",
            ProviderId = "spotify",
            SnapshotVersion = 1,
            PayloadJson = SelfLoveMatchFixture.Payload
        };
        var track = SelfLoveMatchFixture.Local(owner, "backend");
        track.CanonicalRecordingId = recording;
        if (conflict == "version") track.Title += " (Live)";
        if (conflict == "duration") track.DurationMilliseconds = 240000;
        if (conflict == "isrc")
        {
            track.Isrc = "USAAA2600002";
            snapshot.PayloadJson = snapshot.PayloadJson.Replace("\"IsExplicit\":false", "\"IsExplicit\":false,\"Isrc\":\"USAAA2600001\"");
        }

        var projection = TrackRouteProjector.Project(snapshot, null, null, source, [track], [source, deezer, apple],
            ["apple-musickit", "deezer"]);

        Assert.Equal(TrackMatchState.Accepted, projection.ReviewState);
        Assert.Equal(conflict == null ? track.Id : (Guid?)null, projection.LibraryTrack?.Id);
        Assert.Equal(new[] { "apple-musickit", "deezer" }, projection.Classification.ProviderRoutes.Select(item => item.ProviderId));
        Assert.Equal(conflict == null ? null : "apple-musickit", projection.PrimaryProviderRoute?.ProviderId);
    }

    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public void SourcePolicyChangeSuppressesStoredAutomaticMatchesButRetainsExplicitPins(ProviderUseMode mode)
    {
        var canonical = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        var source = Identity(canonical, "source", "original");
        var target = Identity(canonical, "target", "alternative");
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("source", mode) };
        var decision = Decision(local, TrackMatchState.Accepted, .99, .88);
        var automatic = TrackClassifier.Classify(null, decision, source, [source, target], ["source", "target"],
            new HashSet<Guid> { local }, policy);
        Assert.Equal(TrackMatchState.Unresolved, automatic.State);
        Assert.Equal(TrackRouteKind.Unresolved, automatic.RouteKind);
        var pin = new ManualTrackOverrideRecord { Decision = ManualOverrideDecision.Pin, LibraryTrackId = local };
        var explicitLocal = TrackClassifier.Classify(pin, decision, source, [source, target], ["source", "target"],
            new HashSet<Guid> { local }, policy);
        Assert.Equal(TrackMatchState.Pinned, explicitLocal.State);
        Assert.Equal(local, explicitLocal.LibraryTrackId);
    }

    [Fact]
    public void Classify_UsesOneThresholdOverrideAndFallbackPolicy()
    {
        var canonical = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var fallback = Identity(canonical, "qobuz", "fallback");
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
        var canonical = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var pinned = Identity(canonical, "apple-download", "target");
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
        var canonical = Guid.CreateVersion7();
        var local = Guid.CreateVersion7();
        var source = Identity(canonical, "spotify", "source");
        var snapshot = new ExternalMetadataSnapshotRecord { BackendInstanceId = "backend" };
        var track = new LibraryTrackRecord
        {
            Id = local,
            BackendInstanceId = "backend",
            CanonicalRecordingId = canonical,
            BackendLibraryId = "music",
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

    [Fact]
    public void MatchedCanonicalUsesItsVerifiedRoutesWithoutChangingTheSourceIdentity()
    {
        var original = Guid.CreateVersion7();
        var matched = Guid.CreateVersion7();
        var source = Identity(original, "deezer", "source");
        source.Scope = ProviderIdentityScope.Account;
        source.ProviderAccountId = Guid.CreateVersion7();
        var target = Identity(matched, "qobuz", "matched-track");
        var privateTarget = Identity(matched, "qobuz", "other-account");
        privateTarget.Scope = ProviderIdentityScope.Account;
        privateTarget.ProviderAccountId = Guid.CreateVersion7();
        var decision = Decision(Guid.Empty, TrackMatchState.Accepted, 1, .9);
        decision.LibraryTrackId = null;
        decision.CanonicalRecordingId = matched;

        var projection = TrackRouteProjector.Project(new ExternalMetadataSnapshotRecord(),
            decision, null, source, [], [source, target, privateTarget], ["deezer", "qobuz"]);

        Assert.Equal(matched, projection.CanonicalRecordingId);
        var route = Assert.Single(projection.Classification.ProviderRoutes);
        Assert.Equal("qobuz", route.ProviderId);
        Assert.Equal("matched-track", route.ExternalId);
        Assert.Equal(original, source.CanonicalRecordingId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MatchingLibraryCopiesPrecedeCanonicalProviderRoutesAndPreservePersonalPins(bool existingDecision, bool pinned)
    {
        var owner = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var source = Identity(canonical, "spotify", "source");
        var fallback = Identity(canonical, "apple-musickit", "fallback");
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BackendInstanceId = "backend",
            ProviderId = "spotify",
            ExternalIdHash = "source",
            SnapshotVersion = 1,
            PayloadJson = """{"Title":"Self Love (Spider-Man: Across the Spider-Verse)","Artist":"Metro Boomin, Coi Leray","Album":"Metro Boomin Presents Spider-Man: Across the Spider-Verse (Soundtrack From and Inspired by the Motion Picture)"}"""
        };
        var copies = Enumerable.Range(0, 3).Select(index => new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BackendInstanceId = "backend",
            BackendLibraryId = "music",
            BackendItemId = $"copy-{index}",
            CanonicalRecordingId = Guid.NewGuid(),
            Title = "Self Love",
            Artist = "Metro Boomin, Coi Leray",
            Album = "Metro Boomin Presents Spider-Man: Across the Spider-Verse (Soundtrack From and Inspired by the Motion Picture)",
            FilePath = $"/music/{index}.flac",
            SourceModifiedAt = DateTimeOffset.UnixEpoch.AddDays(index)
        }).ToArray();
        var decision = existingDecision ? Decision(Guid.Empty, TrackMatchState.Accepted, .9568, .88) : null;
        if (decision != null) { decision.LibraryTrackId = null; decision.CanonicalRecordingId = canonical; }
        var pin = pinned ? new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Pin,
            OwnerUserId = owner,
            TargetProviderId = "apple-musickit",
            TargetExternalId = "personal-choice"
        } : null;

        var projection = TrackRouteProjector.Project(snapshot, decision, pin, source, copies, [source, fallback], ["apple-musickit"]);

        Assert.Equal(pinned ? TrackMatchState.Pinned : TrackMatchState.Accepted, projection.ReviewState);
        Assert.Equal(pinned ? null : copies[2].Id, projection.LibraryTrack?.Id);
        Assert.Equal(pinned ? "personal-choice" : "fallback", projection.Classification.PrimaryProviderRoute!.ExternalId);
        var removed = TrackRouteProjector.Project(snapshot, decision, pin, source, [], [source, fallback], ["apple-musickit"]);
        Assert.Null(removed.LibraryTrack);
        Assert.Equal(pinned ? "personal-choice" : "fallback", removed.PrimaryProviderRoute!.ExternalId);
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
            VerificationMethod = "test"
        };
}
