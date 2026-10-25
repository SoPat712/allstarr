using System.Text.Json;
using System.Diagnostics;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Core.Capabilities;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class TrackMatchDecisionEngineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("artistgrid", true, false, .05)]
    [InlineData("artistgrid", false, false, 0)]
    [InlineData("deezer", true, false, 0)]
    [InlineData("artistgrid", true, true, 0)]
    public void BestVersionBonusRequiresAnArtistGridExternalRecommendation(string provider, bool best, bool local, double bonus)
    {
        var candidate = Candidate(Scope()) with
        {
            CanonicalRecordingId = null,
            Title = "A Sng",
            Album = "Other album",
            Isrc = null,
            IsLocal = local,
            ExternalProvider = provider,
            IsBestVersion = best
        };
        var engine = new TrackMatchDecisionEngine();
        var baseline = Assert.Single(engine.ScoreCandidates(Source(), [candidate with { IsBestVersion = false }]));
        var score = Assert.Single(engine.ScoreCandidates(Source(), [candidate]));
        Assert.Equal(Math.Min(1, baseline.Confidence + bonus), score.Confidence, 8);
        Assert.Equal(bonus > 0, score.Reasons.Contains("ArtistGrid best version +5"));
        Assert.Equal(bonus > 0, score.IsBestVersion);
    }

    [Fact]
    public void ArtistGridBestVersionBonusCapsAtOneHundredAndRanksFirst()
    {
        var candidate = Candidate(Scope()) with
        {
            CanonicalRecordingId = null,
            IsLocal = false,
            ExternalProvider = "artistgrid"
        };
        var best = candidate with { LibraryTrackId = Guid.CreateVersion7(), IsBestVersion = true };
        var scores = new TrackMatchDecisionEngine().ScoreCandidates(Source(), [candidate, best]);
        Assert.Equal(1, scores[0].Confidence);
        Assert.Contains("ArtistGrid best version +5", scores.Single(score => score.LibraryTrackId == best.LibraryTrackId).Reasons);
        var lower = candidate with { Title = "A Sng", Album = "Other album", Isrc = null };
        scores = new TrackMatchDecisionEngine().ScoreCandidates(Source(),
            [lower, lower with { LibraryTrackId = best.LibraryTrackId, IsBestVersion = true }]);
        Assert.Equal(best.LibraryTrackId, scores[0].LibraryTrackId);
    }

    [Fact]
    public void AWrongVersionCannotMakeTheCorrectRecordingAmbiguous()
    {
        var scope = Scope();
        var source = Source() with { Title = "The Long Road Through The Mountains To The Place We Call Home" };
        var correct = Candidate(scope) with { CanonicalRecordingId = null, Title = source.Title };
        var wrong = correct with { LibraryTrackId = Guid.CreateVersion7(), BackendItemId = "live-version", Title = source.Title + " (Live)" };
        var decision = new TrackMatchDecisionEngine(new TrackMatchPolicy
        { AcceptThreshold = .75, SuggestThreshold = .70, AmbiguityDelta = .25 }).Decide(scope, source, [correct, wrong]);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(correct.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void AManualPinRemainsAuthoritativeDespiteAVersionLabelMismatch()
    {
        var scope = Scope();
        var source = Source();
        var candidate = Candidate(scope) with { Title = source.Title + " (Live)" };
        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [candidate],
            new ScopedTrackMatchOverride(scope.UserId, source.ProviderId, source.ExternalId, candidate.LibraryTrackId));
        Assert.Equal(TrackMatchReviewState.Pinned, decision.State);
        Assert.Equal(candidate.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Instrumental")]
    [InlineData("Acoustic")]
    [InlineData("Remix")]
    [InlineData("Clean")]
    [InlineData("Radio Edit")]
    public void SemanticVersionMismatchCannotBeAutomaticallyAcceptedAtALowerConfiguredThreshold(string version)
    {
        var scope = Scope();
        var source = Source() with { Title = "The Long Road Through The Mountains To The Place We Call Home" };
        var candidate = Candidate(scope) with { CanonicalRecordingId = null, Title = source.Title + " (" + version + ")" };
        var decision = new TrackMatchDecisionEngine(new TrackMatchPolicy { AcceptThreshold = .75, SuggestThreshold = .70 })
            .Decide(scope, source, [candidate]);
        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.True(decision.RequiresReview);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("semantic_version_mismatch", Assert.Single(decision.Candidates).Warnings);
    }

    [Theory]
    [InlineData("spotify", "A SONG", "title_case_folded")]
    [InlineData("apple-music", "A SONG", "title_case_folded")]
    [InlineData("deezer", "A-Song!", "title_normalized")]
    [InlineData("spotify", "A-Song!", "title_normalized")]
    [InlineData("apple-music", "A Song (Official Audio)", "title_decorators_ignored")]
    [InlineData("deezer", "A Song (Official Audio)", "title_decorators_ignored")]
    [InlineData("spotify", "A Sng", "title_fuzzy")]
    public void FieldTitleExplanationsDescribeTheSameNormalizationAcrossProviders(string provider, string title, string reason)
    {
        var scope = Scope();
        var source = Source() with { ProviderId = provider, Title = title };
        var score = Assert.Single(new TrackMatchDecisionEngine().ScoreCandidates(source,
            [Candidate(scope) with { CanonicalRecordingId = null }]));
        Assert.Contains(reason, score.Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOlderManualCandidateRejectionDoesNotBlockADifferentAcceptedCandidate(bool externalMatch)
    {
        var rejected = Guid.CreateVersion7();
        var accepted = Guid.CreateVersion7();
        var manual = new ManualTrackOverrideRecord
        {
            Decision = ManualOverrideDecision.Reject,
            LibraryTrackId = rejected,
            MatcherVersion = "older-matcher"
        };
        Assert.False(TrackMatchOverridePolicy.IsEffectiveRejection(manual,
            new TrackMatchRecord
            {
                State = TrackMatchState.Accepted,
                LibraryTrackId = externalMatch ? null : accepted,
                CanonicalRecordingId = Guid.CreateVersion7(),
                CandidateResultsJson = JsonSerializer.Serialize(new[] { new { LibraryTrackId = rejected } })
            }));
        Assert.True(TrackMatchOverridePolicy.IsEffectiveRejection(manual,
            new TrackMatchRecord { State = TrackMatchState.Accepted, LibraryTrackId = rejected, CandidateResultsJson = "[]" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredCrossLink_FindsIndexedCandidateButDoesNotOverrideConflictingIsrc(bool conflict)
    {
        var scope = Scope();
        var source = Source() with
        {
            Isrc = conflict ? "USRC17607839" : null,
            ExactProviderAliases = new Dictionary<string, string> { ["qobuz"] = "42" }
        };
        var candidate = Candidate(scope) with
        {
            Title = "Different catalog title",
            Artist = "Alternate credits",
            Isrc = conflict ? "GBAYE0601498" : null,
            ProviderTrackIds = new Dictionary<string, string> { ["qobuz"] = "42" }
        };
        var engine = new TrackMatchDecisionEngine();
        var decision = engine.Decide(scope, source, engine.PrepareCandidates([candidate]));
        var score = Assert.Single(decision.Candidates);
        Assert.Contains(conflict ? "isrc_conflict" : "odesli_alias_exact", score.Reasons);
        Assert.Equal(conflict ? TrackMatchReviewState.Unresolved : TrackMatchReviewState.Accepted, decision.State);
        if (conflict) Assert.Null(decision.SelectedLibraryTrackId);
    }

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
    public void PersonalLocalPreferenceCreatesAnIndependentMatcher()
    {
        var scope = Scope();
        var source = Source();
        var local = Candidate(scope);
        var shared = new TrackMatchDecisionEngine();
        var personal = shared.WithLocalPriorityWindow(0.20);

        var personalWindow = personal.ScoreCandidates(source, [local]).Single().Components!["priorityWindow"];
        var sharedWindow = shared.ScoreCandidates(source, [local]).Single().Components!["priorityWindow"];

        Assert.Equal(0.20, personalWindow);
        Assert.Equal(0.07, sharedWindow);
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
    public void ConflictingIsrcsDoNotAcceptTheLeftoverFieldScore()
    {
        var scope = Scope();
        var candidate = Candidate(scope) with { Isrc = "USUM72000714" };

        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source() with { Isrc = "GBUM71029604" },
            [candidate]);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("isrc_conflict", decision.Reasons);
        Assert.Equal(0, decision.Confidence);
    }

    [Fact]
    public void DurationDriftBeyondToleranceIsRejected()
    {
        var scope = Scope();
        var candidate = Candidate(scope) with { DurationMilliseconds = 270_000 };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [candidate]);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Contains("duration_mismatch", decision.Candidates[0].Reasons);
        Assert.True(decision.Candidates[0].Components!["duration"] < 0.5);
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
    public void ReorderedDuetCreditsOnDifferentReleases_DoNotCreateAmbiguity()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Dream A Little Dream Of Me - Single Version",
            Artist = "Ella Fitzgerald, Louis Armstrong",
            Album = "Love, Ella",
            AlbumArtist = null,
            DurationMilliseconds = 185_160
        };
        var first = ProviderCandidate(scope, "apple-download", 186_000) with
        {
            Title = "Dream a Little Dream of Me",
            Artist = "Louis Armstrong & Ella Fitzgerald",
            Album = "Stranger Things: Soundtrack from the Netflix Series, Season 4"
        };
        var second = first with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "another-release",
            Artist = "Ella Fitzgerald & Louis Armstrong",
            Album = "Cheek To Cheek: The Complete Duet Recordings",
            DurationMilliseconds = 187_000
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [first, second]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(2, decision.Candidates.Count);
        Assert.DoesNotContain("ambiguous_top_candidates", decision.Warnings);
        Assert.True(TrackMatchDecisionEngine.SameRecordingIdentity(first, second));
    }

    [Theory]
    [InlineData("Dream a Little Dream of Me", "Ella Fitzgerald & Louis Armstrong", 187_000, true)]
    [InlineData("Dream a Little Dream of Me (Live)", "Ella Fitzgerald & Louis Armstrong", 187_000, false)]
    [InlineData("Dream a Little Dream of Me (Instrumental)", "Ella Fitzgerald & Louis Armstrong", 187_000, false)]
    [InlineData("Dream a Little Dream of Me", "Ella Fitzgerald", 187_000, false)]
    [InlineData("Dream a Little Dream of Me", "Ella Fitzgerald & Another Singer", 187_000, false)]
    [InlineData("Dream a Little Dream of Me", "Ella Fitzgerald & Louis Armstrong", 200_000, false)]
    public void ReleaseEquivalence_PreservesVersionAndCreditDifferences(
        string title, string artist, long duration, bool equivalent)
    {
        var first = ProviderCandidate(Scope(), "apple-download", 186_000) with
        {
            Title = "Dream a Little Dream of Me",
            Artist = "Louis Armstrong & Ella Fitzgerald",
            Album = "First compilation"
        };
        var second = first with { Title = title, Artist = artist, Album = "Another compilation", DurationMilliseconds = duration };

        Assert.Equal(equivalent, TrackMatchDecisionEngine.SameRecordingIdentity(first, second));
    }

    [Fact]
    public void KissMeMore_VocalFixtureIsAccepted_InstrumentalFixtureIsNotEquivalent()
    {
        var scope = Scope();
        var source = Source() with
        {
            SnapshotId = "fixture-kiss-me-more",
            ProviderId = "fixture-provider",
            ExternalId = "fixture-kiss-me-more-vocal",
            Title = "Kiss Me More",
            Artist = "Doja Cat feat. SZA",
            Album = "Planet Her",
            AlbumArtist = "Doja Cat",
            DurationMilliseconds = 208_000
        };
        var vocal = Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "fixture-kiss-me-more-vocal",
            CanonicalRecordingId = null,
            Title = "Kiss Me More",
            Artist = "Doja Cat feat. SZA",
            Album = "Planet Her",
            AlbumArtist = "Doja Cat",
            DurationMilliseconds = 208_000,
            ProviderTrackIds = null,
            IsLocal = false
        };
        var instrumental = vocal with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "fixture-kiss-me-more-instrumental",
            Title = "Kiss Me More (Instrumental)"
        };

        var engine = new TrackMatchDecisionEngine();
        var vocalDecision = engine.Decide(scope, source, [vocal]);
        var instrumentalDecision = engine.Decide(scope, source, [instrumental]);

        Assert.Equal(TrackMatchReviewState.Accepted, vocalDecision.State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, instrumentalDecision.State);
        Assert.False(TrackMatchDecisionEngine.SameRecordingIdentity(vocal, instrumental));
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
            DurationMilliseconds = 332_000,
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
    public void Missing_optional_metadata_stays_unknown_and_limits_field_confidence()
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
        Assert.Equal(.94, score.Confidence);
        Assert.Contains("limited_recording_metadata", score.Warnings);
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
    public void CandidateIndexFindsDecoratedSpotifyTitleInLocalJellyfinLibrary()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Link Up (Metro Boomin & Don Toliver, Wizkid feat. BEAM & Toian) - Spider-Verse Remix (Spider-Man: Across the Spider-Verse )",
            Artist = "Metro Boomin, Don Toliver, Wizkid, BEAM, Toian",
            Album = "METRO BOOMIN PRESENTS SPIDER-MAN: ACROSS THE SPIDER-VERSE (SOUNDTRACK FROM AND INSPIRED BY THE MOTION PICTURE)",
            DurationMilliseconds = 195_386
        };
        var local = Candidate(scope) with
        {
            Title = "Link Up (Spider-Verse remix)",
            Artist = "Metro Boomin, Don Toliver, Wizkid, BEAM, Toian",
            Album = "METRO BOOMIN PRESENTS SPIDER‐MAN: ACROSS THE SPIDER‐VERSE: SOUNDTRACK FROM AND INSPIRED BY THE MOTION PICTURE",
            AlbumArtist = "Metro Boomin",
            DurationMilliseconds = 195_386,
            Isrc = null,
            CanonicalRecordingId = null
        };

        var selected = new TrackMatchCandidateIndex([local]).Select(source);
        var score = Assert.Single(new TrackMatchDecisionEngine().ScoreCandidates(source, selected));

        Assert.Equal(local.LibraryTrackId, score.LibraryTrackId);
        Assert.True(score.Confidence >= 0.9);
        Assert.Equal(0.07, score.Components!["priorityWindow"]);
    }

    [Theory]
    [InlineData("Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix", "The Album", "The Album", .992)]
    [InlineData("Silk & Cologne - EI8HT & Offset - Spider-Verse Remix", "The Album", "The Album", .992)]
    [InlineData("Silk & Cologne - EI8HT & Offset (Spider-Verse remix)", "The Album", "The Album", .992)]
    [InlineData("Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix",
        "Spider-Man: Across the Spider-Verse (Soundtrack From and Inspired by the Motion Picture)",
        "Spider-Man: Across the Spider-Verse (Original Soundtrack From and Inspired by the Motion Picture)", .9909)]
    public void CreditedArtistDecoratorsAndEquivalentRemixLabelsAreAccepted(
        string title, string sourceAlbum, string candidateAlbum, double confidence)
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = title,
            Artist = "EI8HT, Offset",
            Album = sourceAlbum,
            AlbumArtist = null,
            DurationMilliseconds = 222_000
        };
        var candidate = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Title = "Silk and Cologne (Spider-Verse remix)",
            Artist = "Ei8ht, OFFSET",
            Album = candidateAlbum,
            AlbumArtist = null,
            DurationMilliseconds = 221_000
        };
        var engine = new TrackMatchDecisionEngine();
        var selected = engine.PrepareCandidates([candidate]);
        var result = engine.Decide(scope, source, selected);
        var score = Assert.Single(result.Candidates);

        Assert.Equal(confidence, score.Confidence);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(candidate.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(1, score.Components!["title"]);
        Assert.Equal(score.NormalizedSourceTitle, score.NormalizedCandidateTitle);
        Assert.Contains("title_exact", score.Reasons);
        Assert.Contains("title_decorators_ignored", score.Reasons);
        Assert.DoesNotContain("title_fuzzy", score.Reasons);
        Assert.Contains(sourceAlbum == candidateAlbum ? "album_exact" : "album_strong", score.Reasons);
    }

    [Theory]
    [InlineData("Silk and Cologne")]
    [InlineData("Silk and Cologne (Other Producer remix)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Live)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Acoustic)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Sped Up)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Slowed)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Instrumental)")]
    [InlineData("Silk and Cologne (Spider-Verse remix) (Karaoke)")]
    public void CreditedArtistNormalizationPreservesRecordingVersions(string title)
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix",
            Artist = "EI8HT, Offset"
        };
        var result = new TrackMatchDecisionEngine(new TrackMatchPolicy
        {
            AcceptThreshold = .75,
            SuggestThreshold = .70
        }).Decide(scope, source,
            [Candidate(scope) with { CanonicalRecordingId = null, Title = title, Artist = "Ei8ht, OFFSET" }]);

        Assert.Equal(TrackMatchReviewState.Unresolved, result.State);
        Assert.Contains("semantic_version_mismatch", Assert.Single(result.Candidates).Warnings);
    }

    [Fact]
    public void CounterpartArtistCreditsVerifyTitleSideFeaturedArtists()
    {
        var scope = Scope();
        var source = Source() with { Title = "Song (feat. Guest)", Artist = "Main Artist" };
        var candidate = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Title = "Song",
            Artist = "Main Artist, Guest"
        };
        var result = new TrackMatchDecisionEngine().Decide(scope, source, [candidate]);
        var score = Assert.Single(result.Candidates);

        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(0.955, score.Confidence);
        Assert.Equal(1, score.Components!["title"]);
        Assert.Equal("song", score.NormalizedSourceTitle);
        Assert.Equal(score.NormalizedSourceTitle, score.NormalizedCandidateTitle);
        Assert.Contains("title_decorators_ignored", score.Reasons);
        Assert.DoesNotContain("title_annotation_mismatch", score.Warnings);
        Assert.True(TrackMatchDecisionEngine.SameRecordingIdentity(
            candidate with { Title = source.Title, Artist = source.Artist }, candidate));
    }

    [Theory]
    [InlineData("Silk and Cologne (Other Artist) - Spider-Verse Remix")]
    [InlineData("Silk and Cologne (feat. Other Artist) - Spider-Verse Remix")]
    [InlineData("Silk and Cologne - Other Artist - Spider-Verse Remix")]
    public void UncreditedArtistAnnotationsRequireReview(string title)
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix",
            Artist = "EI8HT, Offset"
        };
        var result = new TrackMatchDecisionEngine().Decide(scope, source,
            [Candidate(scope) with { CanonicalRecordingId = null, Title = title, Artist = "Ei8ht, OFFSET" }]);

        Assert.NotEqual(TrackMatchReviewState.Accepted, result.State);
        Assert.True(result.RequiresReview);
        var score = Assert.Single(result.Candidates);
        Assert.Contains("title_annotation_mismatch", score.Warnings);
        Assert.DoesNotContain("semantic_version_mismatch", score.Warnings);
    }

    [Fact]
    public void PriorityWindowDoesNotInflateConfidenceOrAutomaticAcceptance()
    {
        var scope = Scope();
        var source = Source() with { Title = "A Song About The Journey Home" };
        var candidate = Candidate(scope) with { Title = "A Song About Our Journey" };
        var engine = new TrackMatchDecisionEngine();

        var score = Assert.Single(engine.ScoreCandidates(source, [candidate]));
        var decision = engine.Decide(scope, source, [candidate]);

        Assert.True(score.Confidence < decision.AcceptThreshold);
        Assert.Equal(TrackMatchReviewState.Suggested, decision.State);
        Assert.Equal(score.Confidence, decision.Confidence);
    }

    [Fact]
    public void LocalCandidateWithinSevenPointsWinsOverHigherProviderConfidence()
    {
        var scope = Scope();
        var source = Source();
        var local = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Album = null,
            AlbumArtist = null,
            DurationMilliseconds = 245_000
        };
        var provider = ProviderCandidate(scope, "fourth", 240_000);
        var engine = new TrackMatchDecisionEngine();

        var scores = engine.ScoreCandidates(source, [provider, local], ["apple-download", "deezer", "qobuz", "fourth"]);
        var localScore = scores.Single(score => score.LibraryTrackId == local.LibraryTrackId);
        var providerScore = scores.Single(score => score.LibraryTrackId == provider.LibraryTrackId);
        var decision = engine.Decide(
            scope,
            source,
            [provider, local],
            providerPriority: ["apple-download", "deezer", "qobuz", "fourth"]);

        Assert.InRange(providerScore.Confidence - localScore.Confidence, 0.05, 0.07);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(local.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.Equal(local.LibraryTrackId, decision.Candidates[0].LibraryTrackId);
        Assert.Equal(localScore.Confidence, decision.Confidence);
        Assert.Equal(0.07, localScore.Components!["priorityWindow"]);
        Assert.Contains("accepted_route_priority_selected", decision.Reasons);
    }

    [Fact]
    public void AcceptedLocalWinsOutsideItsPreferenceWindow()
    {
        var scope = Scope();
        var source = Source();
        var local = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Album = null,
            AlbumArtist = null,
            DurationMilliseconds = 245_000
        };
        var provider = ProviderCandidate(scope, "deezer", 240_000);
        var raw = new TrackMatchDecisionEngine().ScoreCandidates(source, [provider, local])
            .ToDictionary(score => score.LibraryTrackId);
        var exactWindow = raw[provider.LibraryTrackId].Confidence - raw[local.LibraryTrackId].Confidence;

        var inside = new TrackMatchDecisionEngine(new TrackMatchPolicy
        {
            LocalPriorityWindow = exactWindow
        }).Decide(scope, source, [provider, local]);
        var outside = new TrackMatchDecisionEngine(new TrackMatchPolicy
        {
            LocalPriorityWindow = exactWindow - 0.0002
        }).Decide(scope, source, [provider, local]);

        Assert.Equal(local.LibraryTrackId, inside.SelectedLibraryTrackId);
        Assert.Equal(local.LibraryTrackId, outside.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("apple-download")]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("fourth")]
    public void AcceptedProvidersUseConfiguredOrderInsteadOfScoreWindows(string expectedProvider)
    {
        var scope = Scope();
        var source = Source();
        var order = new[] { expectedProvider, "apple-download", "deezer", "qobuz", "fourth" }.Distinct().ToArray();
        var candidates = order.Select(provider =>
            ProviderCandidate(scope, provider, provider == expectedProvider ? 247_000 : 240_000)).ToArray();

        var decision = new TrackMatchDecisionEngine().Decide(
            scope, source, candidates, providerPriority: order);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(
            candidates.Single(candidate => candidate.ProviderTrackIds?.ContainsKey(expectedProvider) == true).LibraryTrackId,
            decision.SelectedLibraryTrackId);
        Assert.True(decision.Candidates[0].Confidence < decision.Candidates[1].Confidence);
        Assert.Equal(1, decision.Candidates[0].Components!["routingPriority"]);
        Assert.Contains("accepted_route_priority_selected", decision.Reasons);
    }

    [Fact]
    public void ConfiguredProviderUsesItsClosestAcceptedRecordingBeforeLaterProviders()
    {
        var scope = Scope();
        var preferredCloser = ProviderCandidate(scope, "apple-download", 241_000);
        var preferredFarther = ProviderCandidate(scope, "apple-download", 247_000) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "apple-download-farther",
            ProviderTrackIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["apple-download"] = "apple-download-farther"
            }
        };
        var laterExact = ProviderCandidate(scope, "qobuz", 240_000);

        var decision = new TrackMatchDecisionEngine().Decide(
            scope,
            Source(),
            [preferredFarther, laterExact, preferredCloser],
            providerPriority: ["apple-download", "qobuz"]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(preferredCloser.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.True(decision.Candidates[0].Confidence <
                    decision.Candidates.Single(item => item.LibraryTrackId == laterExact.LibraryTrackId).Confidence);
        Assert.Equal(1, decision.Candidates[0].Components!["routingPriority"]);
    }

    [Fact]
    public void SunroofPrefersAcceptedLocalDespiteHigherExternalConfidence()
    {
        var scope = Scope();
        var source = Source() with
        {
            Title = "Sunroof",
            Artist = "Nicky Youre, hey daisy",
            Album = "Sunroof",
            AlbumArtist = null,
            DurationMilliseconds = 163_025
        };
        var local = Candidate(scope) with
        {
            Title = "Sunroof",
            Artist = "Nicky Youre, dazy",
            Album = "Sunroof",
            AlbumArtist = null,
            DurationMilliseconds = 163_025,
            CanonicalRecordingId = null
        };
        var external = ProviderCandidate(scope, "apple-download", 163_000) with
        {
            Title = "Sunroof",
            Artist = "Nicky Youre & hey daisy",
            Album = "Sunroof (Remixes) - EP"
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, source, [external, local],
            providerPriority: ["apple-download", "deezer"]);

        Assert.InRange(decision.Candidates.Single(candidate => candidate.IsLocal).Confidence, 0.88, 0.90);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(local.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.InRange(decision.Confidence, 0.88, 0.90);
        Assert.True(decision.Candidates[1].Confidence - decision.Confidence > 0.07);
        Assert.Contains("accepted_route_priority_selected", decision.Reasons);
    }

    [Fact]
    public void AcceptedExternalBeatsPreferredLocalBelowAcceptanceThreshold()
    {
        var scope = Scope();
        var local = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Album = null,
            AlbumArtist = null,
            DurationMilliseconds = 400_000
        };
        var external = ProviderCandidate(scope, "deezer", 240_000);
        var decision = new TrackMatchDecisionEngine(new TrackMatchPolicy { LocalPriorityWindow = 0.20 })
            .Decide(scope, Source(), [local, external], providerPriority: ["deezer"]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(external.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.True(decision.Candidates.Single(candidate => candidate.IsLocal).Confidence < decision.AcceptThreshold);
    }

    [Fact]
    public void MultipleAcceptanceQualifiedLocalRecordingsRemainAmbiguous()
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
    public void EarlierConfiguredProviderWinsAnEqualConfidenceTie()
    {
        var scope = Scope();
        var first = ProviderCandidate(scope, "apple-download", 240_000);
        var second = ProviderCandidate(scope, "deezer", 240_000);

        var scores = new TrackMatchDecisionEngine().ScoreCandidates(
            Source(), [second, first], ["apple-download", "deezer"]);

        Assert.Equal(first.LibraryTrackId, scores[0].LibraryTrackId);
        Assert.Equal(scores[0].Confidence, scores[1].Confidence);
    }

    [Fact]
    public void ProviderSearchIsSkippedForAnyAcceptedLocalMatch()
    {
        var scope = Scope();
        var source = Source();
        var safe = Candidate(scope) with
        {
            CanonicalRecordingId = null,
            Album = null,
            AlbumArtist = null,
            DurationMilliseconds = 245_000
        };
        var vulnerable = safe with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            DurationMilliseconds = 247_000
        };
        var engine = new TrackMatchDecisionEngine();

        var safeDecision = engine.Decide(scope, source, [safe]);
        var vulnerableDecision = engine.Decide(scope, source, [vulnerable]);

        Assert.True(engine.CanSkipProviderComparison(safeDecision));
        Assert.True(engine.CanSkipProviderComparison(vulnerableDecision));
        Assert.False(engine.CanSkipProviderComparison(engine.Decide(scope, source,
            [safe with { DurationMilliseconds = 400_000 }])));
    }

    [Fact]
    public void ScopedManualPinWinsButCannotCrossUserOrInvisibleLibrary()
    {
        var scope = Scope();
        var visible = Candidate(scope);
        var pin = new ScopedTrackMatchOverride(
            scope.UserId,
            "spotify",
            "source-track",
            visible.LibraryTrackId);

        var pinned = new TrackMatchDecisionEngine().Decide(scope, Source(), [visible], pin);
        var foreignPin = pin with { UserId = Guid.CreateVersion7() };

        Assert.Equal(TrackMatchReviewState.Pinned, pinned.State);
        Assert.Equal(visible.LibraryTrackId, pinned.SelectedLibraryTrackId);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new TrackMatchDecisionEngine().Decide(scope, Source(), [visible], foreignPin));
        Assert.Throws<UnauthorizedAccessException>(() =>
            new TrackMatchDecisionEngine().Decide(scope, Source(), [visible],
                pin with { ExternalId = "other-source-track" }));

        var inaccessibleTarget = new TrackMatchDecisionEngine().Decide(
            scope, Source(), [visible with { BackendLibraryId = "denied-library" }], pin);

        Assert.Equal(TrackMatchReviewState.Unresolved, inaccessibleTarget.State);
        Assert.Null(inaccessibleTarget.SelectedLibraryTrackId);
        Assert.Contains("manual_override_target_not_visible", inaccessibleTarget.Warnings);
    }

    [Fact]
    public void ManualRejectionPersistsAsAReviewStateAndCannotTriggerSelection()
    {
        var scope = Scope();
        var visible = Candidate(scope);
        var rejection = new ScopedTrackMatchOverride(
            scope.UserId,
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
    public void CandidateRejection_SelectsTheNextMatchAndSurvivesMatcherUpgrade()
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
            scope.UserId,
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
        Assert.True(TrackMatchOverridePolicy.IsEffectiveRejection(record, null));
    }

    [Fact]
    public void CandidateVisibility_ExcludesDeniedLibrariesAndBackends()
    {
        var scope = Scope();
        var candidates = new[]
        {
            Candidate(scope) with { BackendLibraryId = "other-library" },
            Candidate(scope) with { BackendInstanceId = "other-backend" }
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), candidates);

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.Empty(decision.Candidates);
        Assert.Contains("no_indexed_candidate", decision.Warnings);
    }

    [Fact]
    public void CandidateVisibility_AcceptsAccessibleLibraryIndexedByAnotherUser()
    {
        var scope = Scope() with
        {
            AccessibleLibraryIds = new HashSet<string>(StringComparer.Ordinal) { "shared-library" }
        };
        var candidate = Candidate(scope) with
        {
            OwnerUserId = Guid.CreateVersion7(),
            BackendLibraryId = "shared-library"
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [candidate]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(candidate.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void CandidateVisibility_ExcludesDeniedLibraryEvenWhenIndexedByCurrentUser()
    {
        var scope = Scope() with
        {
            AccessibleLibraryIds = new HashSet<string>(StringComparer.Ordinal) { "other-library" }
        };
        var candidate = Candidate(scope);

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [candidate]);

        Assert.Equal(scope.UserId, candidate.OwnerUserId);
        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.Null(decision.SelectedLibraryTrackId);
        Assert.Empty(decision.Candidates);
    }

    [Fact]
    public void CandidateVisibility_SelectsOnlyAccessibleCopyOfRecording()
    {
        var scope = Scope() with
        {
            AccessibleLibraryIds = new HashSet<string>(StringComparer.Ordinal) { "accessible-library" }
        };
        var denied = Candidate(scope);
        var accessible = denied with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = "accessible-copy",
            BackendLibraryId = "accessible-library",
            OwnerUserId = Guid.CreateVersion7()
        };

        var decision = new TrackMatchDecisionEngine().Decide(scope, Source(), [denied, accessible]);

        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(accessible.LibraryTrackId, decision.SelectedLibraryTrackId);
        Assert.Equal(accessible.LibraryTrackId, Assert.Single(decision.Candidates).LibraryTrackId);
    }

    [Fact]
    public void CandidateVisibility_EmptyLibraryAccessExcludesLocalAndKeepsAuthorizedExternal()
    {
        var scope = Scope() with
        {
            AccessibleLibraryIds = new HashSet<string>(StringComparer.Ordinal)
        };
        var local = Candidate(scope);
        var external = ProviderCandidate(scope, "deezer", 240_000);
        var outsideBackend = external with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendInstanceId = "other-backend"
        };
        var otherUser = external with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            OwnerUserId = Guid.CreateVersion7()
        };
        var engine = new TrackMatchDecisionEngine();

        var localDecision = engine.Decide(scope, Source(), [local]);
        var externalDecision = engine.Decide(scope, Source(), [local, outsideBackend, otherUser, external]);

        Assert.Equal(TrackMatchReviewState.Unresolved, localDecision.State);
        Assert.Empty(localDecision.Candidates);
        Assert.Equal(TrackMatchReviewState.Accepted, externalDecision.State);
        Assert.Equal(external.LibraryTrackId, externalDecision.SelectedLibraryTrackId);
        Assert.Equal(external.LibraryTrackId, Assert.Single(externalDecision.Candidates).LibraryTrackId);
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

        Assert.Equal(TrackMatchReviewState.Unresolved, decision.State);
        Assert.True(Assert.Single(decision.Candidates).Components!["artist"] < 0.7);
        Assert.Contains("weak_artist_evidence_review", decision.Warnings);
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
    public void LocalCandidatesExposeTheDefaultSevenPointPriorityWindow()
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
        Assert.Equal(0.07, scores[local.LibraryTrackId].Components!["priorityWindow"]);
        Assert.Equal(0, scores[external.LibraryTrackId].Components!["priorityWindow"]);
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
        "backend",
        Guid.CreateVersion7(),
        PolicyVersion: 3,
        SourceSnapshotVersion: 1,
        AccessibleLibraryIds: new HashSet<string>(StringComparer.Ordinal) { "music" });

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

    private static LocalTrackMatchCandidate ProviderCandidate(
        TrackMatchScope scope,
        string provider,
        long durationMilliseconds) => Candidate(scope) with
        {
            LibraryTrackId = Guid.CreateVersion7(),
            BackendItemId = $"{provider}-{durationMilliseconds}",
            CanonicalRecordingId = null,
            Album = null,
            AlbumArtist = null,
            DurationMilliseconds = durationMilliseconds,
            ProviderTrackIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [provider] = $"{provider}-track"
            },
            IsLocal = false
        };

    private static LocalTrackMatchCandidate Candidate(TrackMatchScope scope) => new(
        Guid.CreateVersion7(),
        scope.UserId,
        scope.BackendInstanceId,
        "music",
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
