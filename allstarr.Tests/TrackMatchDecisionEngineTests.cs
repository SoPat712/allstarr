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
