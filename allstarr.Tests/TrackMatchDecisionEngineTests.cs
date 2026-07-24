using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class TrackMatchDecisionEngineTests
{
    [Theory]
    [InlineData("USRC17607839", null, "US-RC1-76-07839", null, "isrc_exact")]
    [InlineData(null, "f4adcc1d-32e6-4f80-b9d5-abc1c21f61c8", null, "F4ADCC1D-32E6-4F80-B9D5-ABC1C21F61C8", "musicbrainz_recording_id_exact")]
    public void ExactIdentitySignals_AreAcceptedWithAnExplanation(
        string? sourceIsrc,
        string? sourceMbid,
        string? candidateIsrc,
        string? candidateMbid,
        string expectedReason)
    {
        var scope = Scope();
        var candidate = Candidate(scope) with
        {
            Isrc = candidateIsrc,
            MusicBrainzRecordingId = candidateMbid
        };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with { Isrc = sourceIsrc, MusicBrainzRecordingId = sourceMbid },
            [candidate]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(candidate.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.Contains(expectedReason, decision.Reasons);
        Assert.Equal(1, decision.SourceSnapshotVersion);
    }

    [Fact]
    public void FuzzyCorpus_AcceptsStrongMatchAndLeavesWeakMatchUnresolved()
    {
        var scope = Scope();
        var source = Source();
        var strong = Candidate(scope) with
        {
            Title = "A Song (Remastered)",
            Artist = "The Artist",
            Album = "The Album",
            DurationSeconds = 241
        };
        var weak = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "weak",
            Title = "Completely Different",
            Artist = "Someone Else",
            Album = "Another Album",
            DurationSeconds = 90
        };
        var engine = new TrackMatchDecisionEngine();

        var accepted = engine.Decide(scope, source, [strong, weak]);
        var unresolved = engine.Decide(scope, source, [weak]);

        Assert.Equal(TrackMatchReviewState.Accepted, accepted.State);
        Assert.Equal(strong.LibraryTrackId, accepted.SelectedLibraryTrackId);
        Assert.Contains("title_exact", accepted.Reasons);
        Assert.Equal(TrackMatchReviewState.Unresolved, unresolved.State);
        Assert.Null(unresolved.SelectedLibraryTrackId);
        Assert.Contains("below_suggestion_threshold", unresolved.Warnings);
    }

    [Fact]
    public void NearTiedCandidates_RemainAmbiguousWithoutAutomaticAction()
    {
        var scope = Scope();
        var first = Candidate(scope);
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "local-2",
            DurationSeconds = 242
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Ambiguous, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("top_candidates_within_ambiguity_delta", decision.Warnings);
    }

    [Fact]
    public void ScopedManualPinWinsButCannotCrossTenantOrInvisibleLibrary()
    {
        var scope = Scope();
        var visible = Candidate(scope);
        var pin = new ScopedTrackMatchOverride(
            scope.TenantId,
            scope.UserId,
            scope.LibraryScopeId,
            "spotify",
            "source-track",
            visible.LibraryTrackId);

        var pinned = new TrackMatchDecisionEngine().Decide(scope, Source(), [visible], pin);
        var foreignPin = pin with { TenantId = Guid.CreateVersion7() };

        Assert.Equal(TrackMatchReviewState.Pinned, pinned.State);
        Assert.Equal(visible.LibraryTrackId, pinned.SelectedLibraryTrackId);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new TrackMatchDecisionEngine().Decide(scope, Source(), [visible], foreignPin));
    }

    [Fact]
    public void ManualRejectionPersistsAsAReviewStateAndCannotTriggerSelection()
    {
        var scope = Scope();
        var visible = Candidate(scope);
        var rejection = new ScopedTrackMatchOverride(
            scope.TenantId,
            scope.UserId,
            scope.LibraryScopeId,
            "spotify",
            "source-track",
            PinnedLibraryTrackId: null,
            RejectedLibraryTrackIds: new HashSet<Guid> { visible.LibraryTrackId });

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [visible], rejection);

        Assert.Equal(TrackMatchReviewState.Rejected, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("manual_override_rejected_all", decision.Warnings);
    }

    [Fact]
    public void CandidateVisibility_ExcludesOtherUsersLibrariesBackendsAndTenants()
    {
        var scope = Scope();
        var candidates = new[]
        {
            Candidate(scope) with { TenantId = Guid.CreateVersion7() },
            Candidate(scope) with { OwnerUserId = Guid.CreateVersion7() },
            Candidate(scope) with { LibraryScopeId = "other-library" },
            Candidate(scope) with { BackendInstanceId = "other-backend" }
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), candidates);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.Empty(decision.Candidates);
        Assert.Contains("no_visible_candidates", decision.Warnings);
    }

    [Fact]
    public void ExactProviderIdentity_MatchesOneRecordingAcrossProviderGraph()
    {
        var scope = Scope();
        var candidate = Candidate(scope) with
        {
            ProviderTrackIds = new Dictionary<string, string>
            {
                ["spotify"] = "source-track",
                ["deezer"] = "target-track",
                ["apple-musickit"] = "apple-track"
            }
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [candidate]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Contains("provider_track_id_exact", decision.Reasons);
    }

    [Theory]
    [InlineData(
        "Heebiejeebies - Bonus",
        "Aminé, Kehlani",
        "Heebiejeebies",
        "Aminé, Kehlani")]
    [InlineData(
        "Homemade Dynamite (Feat. Khalid, Post Malone & SZA) - REMIX",
        "Lorde, Khalid, Post Malone & SZA",
        "Homemade Dynamite (REMIX)",
        "Lorde, Khalid, Post Malone, SZA")]
    public void ReportedDecoratorFailures_AcceptAndExposeComponentScores(
        string sourceTitle,
        string sourceArtist,
        string candidateTitle,
        string candidateArtist)
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = sourceTitle,
            Artist = sourceArtist,
            Album = "Reported fixture",
            AlbumArtist = sourceArtist
        };
        var candidate = Candidate(scope) with
        {
            Title = candidateTitle,
            Artist = candidateArtist,
            Album = "Reported fixture",
            AlbumArtist = candidateArtist
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [candidate]);
        var score = Assert.Single(decision.Candidates);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.False(decision.RequiresReview);
        Assert.True(score.Confidence >= decision.AcceptThreshold);
        Assert.NotNull(score.Components);
        Assert.Equal(1, score.Components["title"]);
        Assert.True(score.Components["artist"] >= 0.85);
        Assert.Contains("title_exact", score.Reasons);
    }

    [Theory]
    [InlineData("Lorde feat. Khalid & SZA", "SZA, Lorde, Khalid")]
    [InlineData("Lorde featuring Khalid, SZA", "Lorde & Khalid & SZA")]
    [InlineData("Lorde ft Khalid with SZA", "Khalid, SZA, Lorde")]
    public void EquivalentArtistCreditSyntax_IsRankedAsTheSameArtistSet(
        string sourceArtist,
        string candidateArtist)
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with { Artist = sourceArtist, AlbumArtist = sourceArtist },
            [Candidate(scope) with { Artist = candidateArtist, AlbumArtist = candidateArtist }]);

        var score = Assert.Single(decision.Candidates);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(1, score.Components!["artist"]);
    }

    [Fact]
    public void ConflictingFeaturedArtist_IsNotAutomaticallyAccepted()
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with
            {
                Artist = "Lorde feat. SZA",
                AlbumArtist = "Lorde"
            },
            [Candidate(scope) with
            {
                Artist = "Lorde feat. Khalid",
                AlbumArtist = "Lorde"
            }]);

        Assert.NotEqual(TrackMatchReviewState.Accepted, decision.State);
        Assert.True(Assert.Single(decision.Candidates).Components!["artist"] < 0.7);
    }

    [Fact]
    public void WeakCandidate_ExposesThresholdAndReviewReason()
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source(),
            [Candidate(scope) with { Title = "Wrong", Artist = "Unknown", Album = "Elsewhere", DurationSeconds = 12 }]);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.True(decision.RequiresReview);
        Assert.Equal(0.88, decision.AcceptThreshold);
        Assert.Equal(0.72, decision.SuggestThreshold);
        Assert.Contains("below_suggestion_threshold", decision.Warnings);
    }

    private static TrackMatchScope Scope() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "backend",
        "music",
        Guid.CreateVersion7(),
        PolicyVersion: 3,
        SourceSnapshotVersion: 1);

    private static ExternalTrackMatchSnapshot Source() => new(
        "snapshot-1",
        "spotify",
        "source-track",
        "A Song",
        "The Artist",
        "The Album",
        "The Artist",
        240,
        null,
        null,
        IsExplicit: false);

    private static LocalTrackMatchCandidate Candidate(TrackMatchScope scope) => new(
        Guid.CreateVersion7(),
        scope.TenantId,
        scope.UserId,
        scope.BackendInstanceId,
        scope.LibraryScopeId,
        "local-1",
        Guid.CreateVersion7(),
        "A Song",
        "The Artist",
        "The Album",
        "The Artist",
        240,
        null,
        null,
        IsExplicit: false);
}
