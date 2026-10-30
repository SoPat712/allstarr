using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class TrackMatchRecordingVersionTests
{
    [Theory]
    [InlineData("amazon", "apple-download", false)]
    [InlineData("amazon", "apple-download", true)]
    [InlineData("qobuz", "deezer", false)]
    public void UncorroboratedPerfectFieldsStayEligibleBelowCorroboratedRecordingEvidence(string sparseProvider, string detailedProvider, bool reverse)
    {
        var source = Source("Power Trip (feat. Miguel)") with
        {
            Artist = "J. Cole, Miguel",
            Album = "Born Sinner (Deluxe Version)",
            DurationMilliseconds = 241160,
            IsExplicit = true
        };
        var sparse = Candidate("Power Trip [feat. Miguel]") with
        {
            Artist = "J. Cole feat. Miguel",
            Album = "",
            DurationMilliseconds = null,
            IsExplicit = true,
            ProviderTrackIds = new Dictionary<string, string> { [sparseProvider] = "sparse" }
        };
        var detailed = Candidate(source.Title) with
        {
            Artist = "J. Cole",
            Album = source.Album,
            DurationMilliseconds = 241000,
            IsExplicit = true,
            ProviderTrackIds = new Dictionary<string, string> { [detailedProvider] = "detailed" }
        };
        var scores = new TrackMatchDecisionEngine().ScoreCandidates(source, reverse ? [detailed, sparse] : [sparse, detailed]);
        var sparseScore = Assert.Single(scores, item => item.LibraryTrackId == sparse.LibraryTrackId);
        var detailedScore = Assert.Single(scores, item => item.LibraryTrackId == detailed.LibraryTrackId);
        Assert.Equal(.94, sparseScore.Confidence);
        Assert.Equal(.9545, detailedScore.Confidence);
        Assert.Contains("limited_recording_metadata", sparseScore.Warnings);
        Assert.DoesNotContain("limited_recording_metadata", detailedScore.Warnings);
        Assert.Equal(1, sparseScore.Components!["title"]);
        Assert.Equal(1, sparseScore.Components["artist"]);
        Assert.Equal(1, sparseScore.Components["primaryArtist"]);
        Assert.DoesNotContain("album", sparseScore.Components.Keys);
        Assert.DoesNotContain("duration", sparseScore.Components.Keys);
        Assert.DoesNotContain("recordingAllowed", sparseScore.Components.Keys);
        AssertAccepted(source, sparse);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(200000L, null)]
    [InlineData(null, 200000L)]
    [InlineData(200000L, 0L)]
    [InlineData(0L, 200000L)]
    public void MissingDurationDoesNotInventEvidenceOrRejectAFieldMatch(long? sourceDuration, long? candidateDuration)
    {
        var source = Source("Espresso") with { DurationMilliseconds = sourceDuration };
        var candidate = Candidate("Espresso") with { DurationMilliseconds = candidateDuration };
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.Equal(.94, outcome.Confidence);
        Assert.Contains("limited_recording_metadata", outcome.Warnings);
        Assert.DoesNotContain("duration", outcome.Components.Keys);
        Assert.DoesNotContain("album", outcome.Components.Keys);
        Assert.DoesNotContain("recordingAllowed", outcome.Components.Keys);
        AssertAccepted(source, candidate);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("album")]
    [InlineData("albumArtist")]
    public void ComparableRecordingCorroborationDoesNotReceiveTheFieldsOnlyCeiling(string evidence)
    {
        var source = Source("Espresso") with { DurationMilliseconds = null, Album = "Short n Sweet", AlbumArtist = "Sabrina Carpenter" };
        var candidate = Candidate(source.Title) with { DurationMilliseconds = null };
        if (evidence == "duration") { source = source with { DurationMilliseconds = 200000 }; candidate = candidate with { DurationMilliseconds = 200000 }; }
        if (evidence == "album") candidate = candidate with { Album = source.Album };
        if (evidence == "albumArtist") candidate = candidate with { AlbumArtist = source.AlbumArtist };
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.Equal(1, outcome.Confidence);
        Assert.DoesNotContain("limited_recording_metadata", outcome.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldsOnlyCeilingNeverBoostsWeakOrVersionPenalizedEvidence(bool differentVersion)
    {
        var source = Source("Espresso") with { DurationMilliseconds = null };
        var candidate = Candidate(differentVersion ? "Espresso (Live)" : "Different Song") with { DurationMilliseconds = null };
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.InRange(outcome.Confidence, 0, .9399);
        Assert.Contains("limited_recording_metadata", outcome.Warnings);
        if (differentVersion)
        {
            Assert.Equal(.7908, outcome.Confidence);
            Assert.Contains("semantic_version_mismatch", outcome.Warnings);
        }
    }

    [Fact]
    public void WeakComparableAlbumDoesNotRemoveTheFieldsOnlyCeiling()
    {
        var source = Source("Espresso") with { DurationMilliseconds = null, Album = "Short n Sweet" };
        var candidate = Candidate(source.Title) with { DurationMilliseconds = null, Album = "Different Album" };
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.Equal(.94, outcome.Confidence);
        Assert.InRange(outcome.Components["album"], 0, .8499);
        Assert.Contains("limited_recording_metadata", outcome.Warnings);
        Assert.DoesNotContain("duration", outcome.Components.Keys);
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("recording")]
    [InlineData("isrc")]
    [InlineData("alias")]
    [InlineData("provider")]
    public void ExactRecordingEvidenceWithMissingFieldsKeepsItsExistingConfidence(string evidence)
    {
        var source = Source("Espresso") with { DurationMilliseconds = null };
        var candidate = Candidate(source.Title) with { DurationMilliseconds = null };
        if (evidence == "canonical") { var id = Guid.NewGuid(); source = source with { CanonicalRecordingId = id }; candidate = candidate with { CanonicalRecordingId = id }; }
        if (evidence == "recording") { source = source with { MusicBrainzRecordingId = "recording" }; candidate = candidate with { MusicBrainzRecordingId = "recording" }; }
        if (evidence == "isrc") { source = source with { Isrc = "USAAA2600001" }; candidate = candidate with { Isrc = source.Isrc }; }
        if (evidence == "alias") source = source with { ExactProviderAliases = candidate.ProviderTrackIds };
        if (evidence == "provider") candidate = candidate with { ProviderTrackIds = new Dictionary<string, string> { [source.ProviderId] = source.ExternalId } };
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.Equal(evidence == "isrc" ? .99 : 1, outcome.Confidence);
        Assert.DoesNotContain("limited_recording_metadata", outcome.Warnings);
    }

    [Theory]
    [InlineData("Espresso", "Espresso (Mixed)", null, 133_000)]
    [InlineData("Espresso", "Espresso [Mixed]", null, 106_000)]
    [InlineData("Espresso", "Espresso - Mixed", null, 84_000)]
    [InlineData("Espresso", "Espresso Mixed", null, 200_000)]
    [InlineData("Levitating", "Levitating (Mixed)", null, 200_000)]
    [InlineData("Shape of You", "Shape of You (Mixed)", null, 200_000)]
    [InlineData("Hotel California", "Hotel California (Mixed)", null, 79_000)]
    [InlineData("Get Lucky", "Get Lucky", "Barbie: Live from the Barbie Dream House (DJ Mix)", 200_000)]
    [InlineData("Espresso", "Espresso", "Summer (Mixed by Someone)", 200_000)]
    [InlineData("Espresso", "Espresso", "Continuous Mix", 200_000)]
    public void ContinuousMixExcerptsAreRejected(string title, string candidateTitle, string? album, long duration)
    {
        AssertRejected(Source(title), Candidate(candidateTitle) with { Album = album, DurationMilliseconds = duration },
            "continuous_mix_excerpt");
    }

    [Theory]
    [InlineData("Espresso (Mixed)", "Espresso (Mixed)", null)]
    [InlineData("Espresso [Mixed]", "Espresso [Mixed]", "DJ Mix")]
    [InlineData("Mixed Emotions", "Mixed Emotions", null)]
    [InlineData("Get Lucky", "Get Lucky", "Summer Compilation")]
    public void RequestedMixesAndOrdinaryCompilationsRemainEligible(string title, string candidateTitle, string? album)
    {
        AssertAccepted(Source(title), Candidate(candidateTitle) with { Album = album });
    }

    [Theory]
    [InlineData("Like Him (Melody Karaoke Version)")]
    [InlineData("Like Him (Instrumental)")]
    [InlineData("Like Him (Originally Performed by Tyler, The Creator)")]
    [InlineData("Like Him (In the Style of Tyler, The Creator)")]
    [InlineData("Like Him (Backing Track)")]
    [InlineData("Like Him (Sing-Along)")]
    public void UnrequestedBackingRecordingsAreRejected(string title)
    {
        AssertRejected(Source("Like Him"), Candidate(title), "backing_recording_mismatch");
        AssertAccepted(Source(title), Candidate(title));
    }

    [Theory]
    [InlineData("Get Lucky - Tribute to Daft Punk", "Other Artist")]
    [InlineData("Get Lucky (Made Famous by Daft Punk)", "Other Artist")]
    [InlineData("Get Lucky (Cover)", "Other Artist")]
    [InlineData("Get Lucky", "Daft Punk Tribute Band")]
    [InlineData("Get Lucky", "Some Tribute Band")]
    [InlineData("Get Lucky", "The Daft Punk Cover Band")]
    public void TributeRecordingsByAnotherMainArtistAreRejected(string title, string artist)
    {
        AssertRejected(Source("Get Lucky") with { Artist = "Daft Punk" },
            Candidate(title) with { Artist = artist }, "cover_artist_mismatch");
    }

    [Theory]
    [InlineData("Get Lucky (Cover)", "Get Lucky (Cover)", "Daft Punk")]
    [InlineData("Get Lucky - Tribute to Daft Punk", "Get Lucky - Tribute to Daft Punk", "Other Artist")]
    public void SameMainArtistDoesNotTriggerTheCoverGuard(string sourceTitle, string title, string artist)
    {
        AssertAccepted(Source(sourceTitle) with { Artist = artist }, Candidate(title) with { Artist = artist });
    }

    [Theory]
    [InlineData("Hours In Silence (Clean)", null)]
    [InlineData("Hours In Silence (Edited)", null)]
    [InlineData("Hours In Silence (Radio Edit)", null)]
    [InlineData("Hours In Silence (Radio-Edit)", null)]
    [InlineData("Hours In Silence (Censored)", null)]
    public void CleanEditsCannotReplaceAnExplicitSource(string title, bool? explicitContent)
    {
        AssertRejected(Source("Hours In Silence") with { IsExplicit = true },
            Candidate(title) with { IsExplicit = explicitContent }, "clean_edit_of_explicit");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    public void ExplicitMetadataAllowsMatchingAndUnknownFlags(bool sourceExplicit, bool? candidateExplicit)
    {
        AssertAccepted(Source("Hours In Silence") with { IsExplicit = sourceExplicit },
            Candidate("Hours In Silence") with { IsExplicit = candidateExplicit });
    }

    [Fact]
    public void CleanCandidateOutranksAnExplicitCandidateFromAPreferredProvider()
    {
        var source = Source("Hours In Silence") with { IsExplicit = false };
        var explicitCandidate = Candidate(source.Title) with { IsExplicit = true };
        var clean = Candidate(source.Title) with
        {
            IsExplicit = false,
            ProviderTrackIds = new Dictionary<string, string> { ["deezer"] = "clean" }
        };
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source,
            [explicitCandidate, clean], providerPriority: ["apple-download", "deezer"]);

        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(clean.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Contains("clean_version_available", result.Candidates.Single(item =>
            item.LibraryTrackId == explicitCandidate.LibraryTrackId).Reasons);
    }

    [Theory]
    [InlineData(191_999, "duration_mismatch")]
    [InlineData(208_001, "duration_mismatch")]
    [InlineData(79_000, "duration_mismatch")]
    public void KnownDurationOutsideTheExistingToleranceIsRejected(long duration, string reason)
    {
        AssertRejected(Source("Hotel California"), Candidate("Hotel California") with
        { DurationMilliseconds = duration }, reason);
    }

    [Theory]
    [InlineData(192_000L)]
    [InlineData(208_000L)]
    [InlineData(null)]
    [InlineData(0L)]
    public void ToleratedOrUnknownDurationsRemainEligible(long? duration)
    {
        AssertAccepted(Source("Espresso"), Candidate("Espresso") with { DurationMilliseconds = duration });
    }

    [Fact]
    public void UnknownSourceDurationDoesNotRejectACandidate()
    {
        AssertAccepted(Source("Espresso") with { DurationMilliseconds = null },
            Candidate("Espresso") with { DurationMilliseconds = 79_000 });
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("recording")]
    [InlineData("isrc")]
    [InlineData("alias")]
    [InlineData("provider")]
    public void ExactIdentifiersCannotBypassTheRecordingGuards(string identity)
    {
        var canonical = Guid.CreateVersion7();
        var source = Source("Espresso") with
        {
            CanonicalRecordingId = identity == "canonical" ? canonical : null,
            MusicBrainzRecordingId = identity == "recording" ? "recording" : null,
            Isrc = identity == "isrc" ? "USUM72403305" : null,
            ExactProviderAliases = identity == "alias" ? new Dictionary<string, string> { ["apple-download"] = "track" } : null
        };
        var candidate = Candidate("Espresso (Mixed)") with
        {
            CanonicalRecordingId = source.CanonicalRecordingId,
            MusicBrainzRecordingId = source.MusicBrainzRecordingId,
            Isrc = source.Isrc,
            ProviderTrackIds = identity == "provider"
                ? new Dictionary<string, string> { ["spotify"] = "source" }
                : new Dictionary<string, string> { ["apple-download"] = "track" }
        };
        AssertRejected(source, candidate, "continuous_mix_excerpt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedMixFallsThroughToTheNextCandidateOrBroaderQuery(bool broaderQuery)
    {
        var source = Source("Espresso");
        var mix = new Song
        {
            ExternalProvider = "apple-download",
            ExternalId = "mix",
            Title = "Espresso (Mixed)",
            Artist = "Sabrina Carpenter",
            Duration = 133
        };
        var original = new Song
        {
            ExternalProvider = "deezer",
            ExternalId = "original",
            Title = source.Title,
            Artist = source.Artist,
            Duration = 200
        };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .Returns((ProtocolExecutionContext _, string query, int _) => Task.FromResult(new MatchingSongSearchResult(
                !broaderQuery ? [mix, original] : query == "Espresso Sabrina Carpenter" ? [mix] : [original], [])));
        var search = new TrackMatchPlayableSearch(gateway.Object, new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "main", "principal",
            new AllstarrPrincipal(User, "jellyfin", "main", "principal", "Owner", false),
            "recording-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        var result = await search.MatchAsync(context, source, Scope(), [], null, CancellationToken.None);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("original", result.SelectedExternal!.ExternalId);
        Assert.Equal("original", Assert.Single(result.RoutableExternalCandidates).ExternalId);
        Assert.Contains("continuous_mix_excerpt", result.Decision.Candidates.Single(item => item.Title == mix.Title).Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanSearchContinuesPastAnExplicitPrimaryResult(bool cleanExists)
    {
        var source = Source("Espresso") with { IsExplicit = false };
        var explicitSong = new Song
        {
            ExternalProvider = "apple-download",
            ExternalId = "explicit",
            Title = source.Title,
            Artist = source.Artist,
            Duration = 200,
            ExplicitContentLyrics = 1
        };
        var clean = new Song
        {
            ExternalProvider = "deezer",
            ExternalId = "clean",
            Title = source.Title,
            Artist = source.Artist,
            Duration = 200,
            ExplicitContentLyrics = 0
        };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .Returns((ProtocolExecutionContext _, string query, int _) => Task.FromResult(new MatchingSongSearchResult(
                query == "Espresso Sabrina Carpenter" || !cleanExists ? [explicitSong] : [clean], [])));
        var search = new TrackMatchPlayableSearch(gateway.Object, new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await search.MatchAsync(Context(), source, Scope(), [], null, CancellationToken.None);

        Assert.Equal(cleanExists ? TrackMatchReviewState.Accepted : TrackMatchReviewState.Suggested, result.Decision.State);
        Assert.Equal(!cleanExists, result.Decision.RequiresReview);
        Assert.Equal(cleanExists ? "clean" : "explicit", result.SelectedExternal!.ExternalId);
        Assert.Equal(cleanExists ? "clean" : "explicit", Assert.Single(result.RoutableExternalCandidates).ExternalId);
        gateway.Verify(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Espresso", 15), Times.Once);
    }

    [Theory]
    [InlineData("Espresso (Mixed)", null)]
    [InlineData("Espresso", false)]
    public async Task CachedWrongRecordingFallsThroughToTheNextProvider(string title, bool? explicitContent)
    {
        var source = Source("Espresso") with { IsExplicit = true };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-download", "wrong"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "apple-download",
                ExternalId = "wrong",
                Title = title,
                Artist = source.Artist,
                Duration = 200,
                ExplicitContentLyrics = explicitContent == false ? 0 : null
            });
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "original"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "deezer",
                ExternalId = "original",
                Title = source.Title,
                Artist = source.Artist,
                Duration = 200,
                ExplicitContentLyrics = 1
            });
        var search = new TrackMatchPlayableSearch(gateway.Object, new TrackMatchDecisionEngine(),
            NullLogger<TrackMatchPlayableSearch>.Instance);

        var result = await search.ReuseAsync(Context(), source, Scope(),
            [new() { ProviderId = "apple-download", ExternalId = "wrong", Verification = ProviderIdentityVerification.Verified },
             new() { ProviderId = "deezer", ExternalId = "original", Verification = ProviderIdentityVerification.Verified }],
            [], null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("original", result.SelectedExternal!.ExternalId);
    }

    [Theory]
    [InlineData("Espresso", "DJ Mix", null)]
    [InlineData("Espresso", null, true)]
    public void RoutingDoesNotGroupADifferentMixOrExplicitVersion(string title, string? album, bool? explicitContent)
    {
        var canonical = Guid.CreateVersion7();
        var original = Candidate("Espresso") with { CanonicalRecordingId = canonical, IsExplicit = false };
        var other = original with { Title = title, Album = album, IsExplicit = explicitContent };
        Assert.False(TrackMatchDecisionEngine.SameRecordingIdentity(original, other));
    }

    [Fact]
    public void RecordingRejectionRemainsUnresolvedAtAZeroSuggestionThreshold()
    {
        var result = new TrackMatchDecisionEngine(new TrackMatchPolicy { SuggestThreshold = 0 }).Decide(
            Scope(), Source("Espresso"), [Candidate("Espresso (Mixed)")]);
        Assert.Equal(TrackMatchReviewState.Unresolved, result.State);
        Assert.Null(result.SelectedLibraryTrackId);
    }

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "main", "principal",
        new AllstarrPrincipal(User, "jellyfin", "main", "principal", "Owner", false),
        "recording-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static void AssertRejected(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate, string reason)
    {
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, new TrackMatchPolicy());
        Assert.Equal(TrackMatchRuleDisposition.Reject, outcome.Disposition);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [candidate]);
        Assert.Equal(TrackMatchReviewState.Unresolved, result.State);
        Assert.Null(result.SelectedLibraryTrackId);
        Assert.Equal(0, result.Confidence);
        Assert.Contains(reason, Assert.Single(result.Candidates).Reasons);
    }

    private static void AssertAccepted(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate)
    {
        var result = new TrackMatchDecisionEngine().Decide(Scope(), source, [candidate]);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(candidate.LibraryTrackId, result.SelectedLibraryTrackId);
    }

    private static readonly Guid User = Guid.CreateVersion7();
    private static TrackMatchScope Scope() => new(User, "main", null, 1, 1, new HashSet<string>());
    private static ExternalTrackMatchSnapshot Source(string title) => new(
        "snapshot", "spotify", "source", title, "Sabrina Carpenter", null, null, 200_000, null, null, null);
    private static LocalTrackMatchCandidate Candidate(string title) => new(
        Guid.CreateVersion7(), User, "main", null, Guid.CreateVersion7().ToString(), null,
        title, "Sabrina Carpenter", null, null, 200_000, null, null, null,
        new Dictionary<string, string> { ["apple-download"] = Guid.CreateVersion7().ToString() }, IsLocal: false);
}
