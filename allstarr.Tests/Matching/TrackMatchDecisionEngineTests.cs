using System.Text.Json;
using System.Diagnostics;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class TrackMatchDecisionEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void Matching_baseline_is_linear_at_100_1000_and_10000_tracks()
    {
        var baselines = new List<(int Count, long Allocated, long ElapsedTicks)>();
        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            var scope = Scope();
            var candidates = Enumerable.Range(0, count)
                .Select(index => Candidate(scope) with
                {
                    LibraryTrackId = Guid.CreateVersion7(),
                    BackendItemId = $"local-{index}",
                    Title = $"Track {index}",
                    Isrc = $"USAAA26{index:D5}"
                })
                .ToArray();
            var sources = candidates.Select((candidate, index) =>
                Source() with
                {
                    SnapshotId = index.ToString(),
                    Title = candidate.Title,
                    Isrc = candidate.Isrc
                }).ToArray();
            var engine = new TrackMatchDecisionEngine();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var prepared = engine.PrepareCandidates(candidates);
            var decisions = sources.Select(source => engine.Decide(scope, source, prepared)).ToArray();
            timer.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(count, decisions.Length);
            Assert.All(decisions, decision => Assert.Equal(TrackMatchReviewState.Accepted, decision.State));
            Assert.All(decisions, decision => Assert.Single(decision.Candidates));
            baselines.Add((count, allocated, timer.ElapsedTicks));
            output.WriteLine(
                $"matching tracks={count} allocated_bytes={allocated} elapsed_ticks={timer.ElapsedTicks}");
        }

        Assert.All(baselines.Zip(baselines.Skip(1)), pair =>
            Assert.True(pair.Second.Allocated < pair.First.Allocated * 30,
                $"Allocation growth from {pair.First.Count} to {pair.Second.Count} tracks was quadratic."));
    }

    [Fact]
    public void PreparedCandidatesAndPersistenceInputPreserveOneDecision()
    {
        var scope = Scope();
        var candidate = Candidate(scope);
        var engine = new TrackMatchDecisionEngine();
        var candidates = engine.PrepareCandidates([candidate]);

        var decision = engine.Decide(scope, Source(), candidates);
        var input = MatchDecisionInput.FromDecision(
            Guid.CreateVersion7(),
            candidate.CanonicalRecordingId,
            decision,
            decisionVersion: 3,
            sourceSnapshotVersion: 2,
            libraryIndexRevision: candidates.Revision,
            policyVersion: "shared-policy");

        Assert.Equal(candidate.LibraryTrackId, input.LibraryTrackId);
        Assert.Equal(TrackMatchState.Accepted, input.State);
        Assert.Equal(decision.AcceptThreshold, input.Threshold);
        Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, input.MatcherVersion);
        Assert.Equal(decision.Candidates.Count,
            JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(input.CandidateResultsJson)!.Length);
    }

    [Fact]
    public void ReviewScoresUseTheMatcherAndReturnHighestConfidenceFirst()
    {
        var scope = Scope();
        var exact = Candidate(scope);
        var weak = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "weak",
            Title = "Different",
            Artist = "Someone Else"
        };

        var scores = new TrackMatchDecisionEngine().ScoreCandidates(Source(), [weak, exact]);

        Assert.Equal(exact.LibraryTrackId, scores[0].LibraryTrackId);
        Assert.True(scores[0].Confidence > scores[1].Confidence);
    }

    [Fact]
    public void LibraryIndexRevision_IsStableAndChangesWithMatchableMetadata()
    {
        var scope = Scope();
        var candidate = Candidate(scope);

        var first = TrackMatchDecisionEngine.LibraryIndexRevision([candidate]);
        var reordered = TrackMatchDecisionEngine.LibraryIndexRevision([candidate]);
        var changed = TrackMatchDecisionEngine.LibraryIndexRevision(
            [candidate with { Title = "Changed" }]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }

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
    public void VerifiedProviderIdentity_IsAccepted()
    {
        var scope = Scope();
        var candidate = Candidate(scope) with
        {
            ProviderTrackIds = new Dictionary<string, string>
            {
                ["spotify"] = "source-track"
            }
        };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope, Source(), [candidate]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Contains("provider_track_id_exact", decision.Reasons);
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
            DurationMilliseconds = 241_000
        };
        var weak = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "weak",
            Title = "Completely Different",
            Artist = "Someone Else",
            Album = "Another Album",
            DurationMilliseconds = 90_000
        };
        var engine = new TrackMatchDecisionEngine();

        var accepted = engine.Decide(scope, source, [strong, weak]);
        var unresolved = engine.Decide(scope, source, [weak]);

        Assert.Equal(TrackMatchReviewState.Accepted, accepted.State);
        Assert.Equal(strong.LibraryTrackId, accepted.SelectedLibraryTrackId);
        Assert.Contains("title_exact", accepted.Reasons);
        var diagnostics = accepted.Candidates[0];
        Assert.Equal("A Song (Remastered)", diagnostics.Title);
        Assert.Equal("a song", diagnostics.NormalizedCandidateTitle);
        Assert.Equal(1, diagnostics.ArtistOverlap);
        Assert.Equal(1_000, diagnostics.DurationDeltaMilliseconds);
        Assert.NotNull(diagnostics.Components);
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
            CanonicalRecordingId = Guid.CreateVersion7(),
            DurationMilliseconds = 240_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Ambiguous, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("ambiguous_top_candidates", decision.Warnings);
    }

    [Fact]
    public void HigherScoringCandidateOutsideNarrowMarginWins()
    {
        var scope = Scope();
        var first = Candidate(scope);
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "local-2",
            CanonicalRecordingId = Guid.CreateVersion7(),
            DurationMilliseconds = 242_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(first.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void DurationEvidenceBreaksOtherwiseExactLongFormTie()
    {
        var scope = Scope();
        var closer = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "closer",
            CanonicalRecordingId = Guid.CreateVersion7(),
            DurationMilliseconds = 327_000,
            IsLocal = false
        };
        var farther = closer with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "farther",
            CanonicalRecordingId = Guid.CreateVersion7(),
            DurationMilliseconds = 405_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with { DurationMilliseconds = 338_000 },
            [farther, closer]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(closer.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.True(decision.Candidates[0].Confidence > decision.Candidates[1].Confidence);
    }

    [Fact]
    public void StableRecordingEvidenceCanCollapseDuplicateInternalCanonicalIds()
    {
        var scope = Scope();
        var first = Candidate(scope) with
        {
            CanonicalRecordingId = Guid.CreateVersion7(),
            MusicBrainzRecordingId = "571c58f1-fba5-4e02-94c9-b54ff0e2d52f",
            DurationMilliseconds = 239_000
        };
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "duplicate",
            CanonicalRecordingId = Guid.CreateVersion7(),
            MusicBrainzRecordingId = null,
            DurationMilliseconds = 241_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Contains(decision.SelectedLibraryTrackId, new Guid?[]
        {
            first.LibraryTrackId,
            second.LibraryTrackId
        });
    }

    [Fact]
    public void DuplicateCopiesWithoutStrongIdentityDoNotBlockBestMatch()
    {
        var scope = Scope();
        var first = Candidate(scope);
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "local-2",
            CanonicalRecordingId = null,
            DurationMilliseconds = 241_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(first.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void SameMusicBrainzRecordingCandidates_DoNotTriggerAmbiguity()
    {
        var scope = Scope();
        var first = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            MusicBrainzRecordingId = "c37ae419-a9a1-4a89-8c3f-d9cadceb8d7f",
            ProviderTrackIds = new Dictionary<string, string>
            {
                ["musicbrainzrecording"] = "16ba7915-2acf-42b2-8c87-ed67090dca91"
            }
        };
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "local-2",
            MusicBrainzRecordingId = "2b9f56d8-0a1a-4d84-bcc9-67d29641ba30",
            ProviderTrackIds = new Dictionary<string, string>
            {
                ["MusicBrainzRecording"] = "16BA79152ACF42B28C87ED67090DCA91"
            }
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [first, second]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Contains(decision.SelectedLibraryTrackId, new Guid?[]
        {
            first.LibraryTrackId,
            second.LibraryTrackId
        });
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public void ClassicalCreditVariantsOnTheSameAlbum_DoNotTriggerAmbiguity()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "21 Hungarian Dances, WoO 1: Hungarian Dance No. 5 in G Minor. Allegro (Orch. Schmeling)",
            Artist = "Claudio Abbado, Wiener Philharmoniker",
            Album = "Brahms: 21 Hungarian Dances",
            DurationMilliseconds = 138_706
        };
        var conductor = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Title = $"Brahms: {source.Title}",
            Artist = "Claudio Abbado",
            Album = source.Album,
            DurationMilliseconds = 139_000,
            IsLocal = false
        };
        var orchestra = conductor with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "deezer-4158796",
            Title = $"21 Hungarian Dances, WoO 1 : Brahms: {source.Title}",
            Artist = "Wiener Philharmoniker",
            DurationMilliseconds = 137_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope, source, [conductor, orchestra]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public void FullArtistCreditsAndAlbumBreakCompilationTie()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Hit 'Em Up - Single Version",
            Artist = "2Pac, Outlawz",
            Album = "Greatest Hits",
            AlbumArtist = null,
            DurationMilliseconds = 313_000
        };
        var preferred = Candidate(scope) with
        {
            Title = "Hit ’Em Up",
            Artist = "2Pac, The Outlawz",
            Album = "Greatest Hits",
            AlbumArtist = "2Pac",
            DurationMilliseconds = 313_000
        };
        var compilation = preferred with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "compilation",
            Artist = "2Pac",
            Album = "Death Row: Greatest Hits",
            AlbumArtist = "Various Artists"
        };
        var candidates = new TrackMatchCandidateIndex([preferred, compilation]).Select(source);

        var decision = new TrackMatchDecisionEngine().Decide(scope, source, candidates);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(preferred.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.True(decision.Candidates[0].Confidence - decision.Candidates[1].Confidence > decision.AmbiguityDelta);
    }

    [Theory]
    [InlineData("PiLlOwT4lK")]
    [InlineData("P1ll0wtalk")]
    public void PillowtalkLookalikeTitle_IsRetrievedAndAccepted(string jellyfinTitle)
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "PILLOWTALK",
            Artist = "ZAYN",
            Album = "Mind of Mine",
            AlbumArtist = "ZAYN",
            DurationMilliseconds = 203_000
        };
        var candidate = Candidate(scope) with
        {
            Title = jellyfinTitle,
            Artist = "ZAYN",
            Album = "Mind of Mine",
            AlbumArtist = "ZAYN",
            DurationMilliseconds = 203_000
        };

        var selected = new TrackMatchCandidateIndex([candidate]).Select(source);
        var decision = new TrackMatchDecisionEngine().Decide(scope, source, selected);

        Assert.Same(candidate, Assert.Single(selected));
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(1, Assert.Single(decision.Candidates).Components!["title"]);
    }

    [Fact]
    public void Exact_title_primary_artist_and_duration_accept_single_album_variants()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "rockstar",
            Artist = "Post Malone, 21 Savage",
            Album = "rockstar",
            DurationMilliseconds = 218_320
        };
        var candidate = Candidate(scope) with
        {
            Title = "rockstar",
            Artist = "Post Malone",
            Album = "beerbongs & bentleys",
            DurationMilliseconds = 218_146
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [candidate]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(candidate.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void Missing_optional_metadata_is_neutral_and_not_reported_as_zero()
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with
            {
                Album = "You'll Be Alright, Kid (Chapter 1)",
                AlbumArtist = null,
                DurationMilliseconds = null
            },
            [Candidate(scope) with
            {
                Album = null,
                AlbumArtist = null,
                DurationMilliseconds = null
            }]);

        var score = Assert.Single(decision.Candidates);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(1, score.Confidence);
        Assert.Contains("artist", score.Components!.Keys);
        Assert.Contains("title", score.Components.Keys);
        Assert.DoesNotContain("album", score.Components.Keys);
        Assert.DoesNotContain("albumArtist", score.Components.Keys);
        Assert.DoesNotContain("duration", score.Components.Keys);
        Assert.Null(score.AlbumEvidence);
    }

    [Fact]
    public void Album_mismatch_cannot_reduce_exact_core_identity()
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with
            {
                Album = "You'll Be Alright, Kid (Chapter 1)",
                AlbumArtist = null,
                DurationMilliseconds = 186_964
            },
            [Candidate(scope) with
            {
                Album = "Ordinary",
                AlbumArtist = "Alex Warren",
                DurationMilliseconds = 186_964
            }]);

        var score = Assert.Single(decision.Candidates);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(1, score.Confidence);
        Assert.Contains("album", score.Components!.Keys);
        Assert.DoesNotContain("albumArtist", score.Components.Keys);
    }

    [Theory]
    [InlineData("A Song (Live)", "A Song")]
    [InlineData("A Song", "A Song (Acoustic)")]
    [InlineData("A Song (Remix)", "A Song")]
    [InlineData("A Song (Instrumental)", "A Song")]
    [InlineData("A Song", "A Song (Stripped)")]
    [InlineData("A Song (Clean)", "A Song (Explicit)")]
    public void SemanticVersionsRemainNegativeEvidence(string sourceTitle, string candidateTitle)
    {
        var scope = Scope();
        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with { Title = sourceTitle },
            [Candidate(scope) with { Title = candidateTitle }]);

        Assert.NotEqual(TrackMatchReviewState.Accepted, decision.State);
        Assert.Contains("semantic_version_mismatch", Assert.Single(decision.Candidates).Warnings);
    }

    [Fact]
    public void CandidateIndexNormalizesUnicodeCreditsAndReleaseDecorators()
    {
        var scope = Scope();
        var candidate = Candidate(scope) with
        {
            Title = "Beyonce's Song",
            Artist = "Artist, Guest"
        };
        var source = Source() with
        {
            Title = "Beyoncé’s Song - 2004 Remaster",
            Artist = "Ártist feat. Guest"
        };

        var selected = new TrackMatchCandidateIndex([candidate]).Select(source);
        var decision = new TrackMatchDecisionEngine().Decide(scope, source, selected);

        Assert.Same(candidate, Assert.Single(selected));
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
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
    public void CandidateRejection_SelectsTheNextMatchAndExpiresWithMatcherVersion()
    {
        var scope = Scope();
        var rejected = Candidate(scope) with
        {
            ProviderTrackIds = new Dictionary<string, string> { ["spotify"] = "source-track" }
        };
        var alternate = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "alternate"
        };
        var manual = new ScopedTrackMatchOverride(
            scope.TenantId,
            scope.UserId,
            scope.LibraryScopeId,
            "spotify",
            "source-track",
            null,
            new HashSet<Guid> { rejected.LibraryTrackId });

        var decision = new TrackMatchDecisionEngine().Decide(
            scope, Source(), [rejected, alternate], manual);
        var record = new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Reject,
            LibraryTrackId = rejected.LibraryTrackId,
            MatcherVersion = "retired"
        };

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(alternate.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.False(TrackMatchOverridePolicy.IsEffectiveRejection(record, null));
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
        Assert.Contains("no_indexed_candidate", decision.Warnings);
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
            [Candidate(scope) with { Title = "Wrong", Artist = "Unknown", Album = "Elsewhere", DurationMilliseconds = 12_000 }]);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.True(decision.RequiresReview);
        Assert.Equal(0.88, decision.AcceptThreshold);
        Assert.Equal(0.72, decision.SuggestThreshold);
        Assert.Contains("below_suggestion_threshold", decision.Warnings);
    }

    [Fact]
    public void Local_candidates_receive_the_default_seven_point_preference()
    {
        var scope = Scope();
        var external = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            Title = "A Songs",
            DurationMilliseconds = null,
            Album = null,
            AlbumArtist = null,
            IsLocal = false
        };
        var local = external with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "local-preferred",
            IsLocal = true
        };

        var scores = new TrackMatchDecisionEngine()
            .ScoreCandidates(Source(), [external, local])
            .ToDictionary(item => item.LibraryTrackId);

        Assert.Equal(
            scores[external.LibraryTrackId].Confidence,
            scores[local.LibraryTrackId].Confidence);
        Assert.Equal(0.07, scores[local.LibraryTrackId].Components!["localPreference"]);
        Assert.Equal(
            Math.Min(1, scores[local.LibraryTrackId].Confidence + 0.07),
            scores[local.LibraryTrackId].Components!["preferenceScore"]);
        Assert.DoesNotContain(
            "localPreference",
            scores[external.LibraryTrackId].Components?.Keys ?? []);
    }

    [Fact]
    public void LargeLibrary_RetainsOnlyTopReviewCandidates()
    {
        var scope = Scope();
        var candidates = Enumerable.Range(0, 25)
            .Select(index => Candidate(scope) with
            {
                LibraryTrackId = Guid.CreateVersion7(),
                BackendItemId = $"local-{index}",
                Title = $"Candidate {index}"
            })
            .ToArray();

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), candidates);

        Assert.Equal(20, decision.Candidates.Count);
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
        240_000,
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
        240_000,
        null,
        null,
        IsExplicit: false);
}
