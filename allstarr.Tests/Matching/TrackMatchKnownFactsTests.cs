using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;

namespace allstarr.Tests;

public sealed class TrackMatchKnownFactsTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(true, null, 240_000L)]
    [InlineData(null, true, null)]
    public void UnknownFactsPreserveStreamingPriority(bool? sourceExplicit, bool? candidateExplicit, long? duration)
    {
        var source = Source() with { IsExplicit = sourceExplicit, DurationMilliseconds = duration };
        var preferred = Candidate("apple-download") with { IsExplicit = candidateExplicit, DurationMilliseconds = null };
        var stronger = Candidate("deezer") with { IsExplicit = sourceExplicit };
        var result = Decide(source, preferred, stronger);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(preferred.LibraryTrackId, result.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OppositeExplicitnessNeedsReviewEvenWithExactIdentity(bool sourceExplicit)
    {
        var source = Source() with { IsExplicit = sourceExplicit, Isrc = "USAAA2600001" };
        var opposite = Candidate("apple-download") with { IsExplicit = !sourceExplicit, Isrc = source.Isrc };
        var result = Decide(source, opposite);
        Assert.Equal(TrackMatchReviewState.Suggested, result.State);
        Assert.True(result.RequiresReview);
        Assert.Equal(opposite.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Contains("explicitness_mismatch_review", result.Warnings);
        var matching = Candidate("deezer") with { IsExplicit = sourceExplicit };
        Assert.Equal(matching.LibraryTrackId, Decide(source, opposite, matching).SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitnessReviewStillRequiresOtherRecordingEvidenceToQualify(bool durationConflict)
    {
        var source = Source() with { Isrc = "USAAA2600001" };
        var opposite = Candidate("apple-download") with
        {
            IsExplicit = true,
            Isrc = durationConflict ? source.Isrc : "USAAA2600002",
            DurationMilliseconds = durationConflict ? 260_000 : source.DurationMilliseconds
        };
        Assert.Equal(TrackMatchReviewState.Unresolved, Decide(source, opposite).State);
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Ao Vivo")]
    [InlineData("En Vivo")]
    [InlineData("En Directo")]
    [InlineData("Live at Wembley")]
    [InlineData("Live from Paris")]
    [InlineData("Remix")]
    [InlineData("Club Mix")]
    [InlineData("Edit")]
    [InlineData("Acoustic")]
    [InlineData("Instrumental")]
    [InlineData("Karaoke")]
    [InlineData("Sped Up")]
    [InlineData("Slowed + Reverb")]
    [InlineData("Nightcore")]
    [InlineData("Demo")]
    [InlineData("Extended")]
    [InlineData("Cover")]
    public void RecordingMarkersGuardLocalAndExactIdentityPaths(string marker)
    {
        var source = Source() with { Isrc = "USAAA2600001" };
        var variant = Candidate("apple-download") with { Album = "Album (" + marker + ")", Isrc = source.Isrc };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, variant).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, variant with { IsLocal = true, BackendLibraryId = "music" }).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source with { Album = variant.Album, DurationMilliseconds = null }, variant with { Album = "Album" }).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceAlbumOnlyMarkerAllowsMatchingDurationAcrossIdentityPaths(bool local)
    {
        var source = Source() with { Album = "Album (Live / Deluxe)", Isrc = "USAAA2600001" };
        var candidate = Candidate("apple-download") with
        {
            Isrc = source.Isrc,
            IsLocal = local,
            BackendLibraryId = local ? "music" : null,
            DurationMilliseconds = 248_000
        };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
    }

    [Theory]
    [InlineData("Instrumental", false)]
    [InlineData("Instrumental", true)]
    [InlineData("Karaoke", false)]
    [InlineData("A Cappella", false)]
    public void SourceAlbumVocalArrangementNeedsConfirmationDespiteMatchingDuration(string marker, bool local)
    {
        var source = Source() with { Title = "Up", Artist = "Cardi B", Album = "Up (" + marker + ")", Isrc = "USAAA2600001" };
        var candidate = Candidate("apple-download") with
        {
            Title = source.Title,
            Artist = source.Artist,
            Album = "Up",
            Isrc = source.Isrc,
            IsLocal = local,
            BackendLibraryId = local ? "music" : null
        };
        foreach (var duration in new long?[] { 240_000, null })
        {
            var result = Decide(source with { DurationMilliseconds = duration }, candidate);
            Assert.Equal(TrackMatchReviewState.Suggested, result.State);
            Assert.Equal(candidate.LibraryTrackId, result.SelectedLibraryTrackId);
            Assert.Contains("source_album_marker_confirmation", result.Warnings);
            Assert.False(TrackMatchDecisionEngine.IsAcceptedExternalCandidate(result.Candidates[0], result.AcceptThreshold));
        }
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate with { Album = source.Album }).State);
        var marked = candidate with { LibraryTrackId = Guid.NewGuid(), Album = source.Album };
        Assert.Equal(marked.LibraryTrackId, Decide(source, candidate, marked).SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("Instrumental", true, null)]
    [InlineData("Karaoke", true, null)]
    [InlineData("Instrumental", false, "spotify")]
    [InlineData("Karaoke", false, "spotify")]
    public void PositiveVocalEvidenceResolvesAlbumOnlyArrangementLabels(string marker, bool explicitSource, string? provenance)
    {
        var source = Source() with
        {
            Title = "Up",
            Artist = "Cardi B",
            Album = "Up (" + marker + ")",
            DurationMilliseconds = 156_946,
            DurationProvenance = provenance,
            IsExplicit = explicitSource
        };
        var apple = Candidate("apple-download") with
        {
            Title = "Up",
            Artist = "Cardi B",
            Album = "AM I THE DRAMA?",
            DurationMilliseconds = 156_000,
            IsExplicit = explicitSource,
            Isrc = "USAT22100061"
        };
        var clean = apple with { LibraryTrackId = Guid.NewGuid(), Album = "Up - Single", IsExplicit = false, Isrc = "USAT22100272" };
        var result = Decide(source, apple, explicitSource ? clean : Candidate("deezer") with
        {
            Title = apple.Title,
            Artist = apple.Artist,
            Album = apple.Album,
            DurationMilliseconds = apple.DurationMilliseconds,
            Isrc = apple.Isrc
        });
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(apple.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.DoesNotContain("source_album_marker_confirmation", result.Warnings);
    }

    [Fact]
    public void BetterMatchingInstrumentalPreventsDurationOnlyVocalRescue()
    {
        var source = Source() with { Album = "Song (Instrumental)", DurationProvenance = "spotify" };
        var vocal = Candidate("apple-download") with { DurationMilliseconds = 239_000 };
        var instrumental = Candidate("deezer") with { Album = source.Album };
        var result = Decide(source, vocal, instrumental);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(instrumental.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(0, result.Candidates.Single(item => item.LibraryTrackId == vocal.LibraryTrackId).Components!["sourceAlbumConfirmation"]);
        Assert.Equal(TrackMatchReviewState.Suggested, Decide(source with { DurationProvenance = "listenbrainz" }, vocal).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted,
            Decide(source with { Title = "Song (Instrumental)", IsExplicit = true }, vocal with { IsExplicit = true }).State);
    }

    [Fact]
    public void DescriptiveFromAnnotationKeepsStreamingPriority()
    {
        var source = Source() with { Title = "Magic - From \"Hexed\"", Artist = "Hailee Steinfeld", Album = "Magic (From \"Hexed\")" };
        var apple = Candidate("apple-download") with { Title = "Magic (From \"Hexed\")", Artist = source.Artist, Album = source.Album, Isrc = "USWD12642617" };
        var deezer = Candidate("deezer") with { Title = apple.Title, Artist = apple.Artist, Album = apple.Album, Isrc = apple.Isrc };
        var result = Decide(source, deezer, apple);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(apple.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, apple with { Title = "Magic (From \"Other Film\")" }).State);
    }

    [Theory]
    [InlineData("Clean", null, TrackMatchReviewState.Accepted)]
    [InlineData("Clean", false, TrackMatchReviewState.Accepted)]
    [InlineData("Clean", true, TrackMatchReviewState.Suggested)]
    [InlineData("Explicit", null, TrackMatchReviewState.Accepted)]
    [InlineData("Explicit", true, TrackMatchReviewState.Accepted)]
    [InlineData("Explicit", false, TrackMatchReviewState.Suggested)]
    public void CandidateAlbumExplicitnessLabelsUseExistingReviewRules(string label, bool? sourceExplicit, TrackMatchReviewState expected)
    {
        var source = Source() with { Title = "Good Time", Artist = "Owl City", Album = "Beach Music", IsExplicit = sourceExplicit };
        var candidate = Candidate("apple-download") with
        {
            Title = source.Title,
            Artist = source.Artist,
            Album = "Beach Music (" + label + ")",
            IsExplicit = null
        };
        var result = Decide(source, candidate);
        Assert.Equal(expected, result.State);
        Assert.Equal(candidate.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(label == "Explicit", result.Candidates[0].IsExplicit);
        Assert.Equal(expected == TrackMatchReviewState.Suggested, result.Warnings.Contains("explicitness_mismatch_review"));
    }

    [Theory]
    [InlineData("Clean", true)]
    [InlineData("Explicit", false)]
    public void CandidateExplicitFlagOverridesAlbumLabel(string label, bool candidateExplicit)
    {
        var source = Source() with { IsExplicit = candidateExplicit, Isrc = "USAAA2600001" };
        var candidate = Candidate("apple-download") with { Album = "Album (" + label + ")", IsExplicit = candidateExplicit, Isrc = source.Isrc };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
    }

    [Theory]
    [InlineData(null, 240_000L)]
    [InlineData(240_000L, null)]
    [InlineData(240_000L, 248_001L)]
    public void SourceAlbumOnlyMarkerStillBlocksUnknownOrConflictingDuration(long? sourceDuration, long? candidateDuration)
    {
        var source = Source() with { Album = "Album (Slowed Down)", DurationMilliseconds = sourceDuration };
        var candidate = Candidate("apple-download") with { DurationMilliseconds = candidateDuration };
        Assert.Equal(TrackMatchReviewState.Unresolved, Decide(source, candidate).State);
    }

    [Fact]
    public void SourceTitleMarkerStillBlocksMatchingDuration()
    {
        var source = Source() with { Title = "Song (Live)", Album = "Album (Live)", Isrc = "USAAA2600001" };
        Assert.Equal(TrackMatchReviewState.Unresolved, Decide(source, Candidate("apple-download") with { Isrc = source.Isrc }).State);
    }

    [Fact]
    public void SlowedAlbumMarkerCannotOverrideAKnownDurationConflict()
    {
        var source = Source() with { Title = "NOBLE", Artist = "F3miii", Album = "NOBLE (Slowed Down)", DurationMilliseconds = 205_000 };
        var candidate = Candidate("apple-download") with { Title = source.Title, Artist = source.Artist, Album = "NOBLE - Single", DurationMilliseconds = 185_000 };
        var result = Decide(source, candidate);
        Assert.Equal(TrackMatchReviewState.Unresolved, result.State);
        Assert.Contains("duration_mismatch", result.Candidates[0].Reasons);
    }

    [Fact]
    public void SourceAlbumMarkerBreaksTiesWithinTheSameProviderRank()
    {
        var source = Source() with { Album = "Album (Live / Deluxe)" };
        var studio = Candidate("apple-download");
        var marked = studio with { LibraryTrackId = Guid.NewGuid(), Album = "Other Album (Live)", DurationMilliseconds = 241_000 };
        var lower = Candidate("deezer") with { Album = source.Album };
        Assert.Equal(studio.LibraryTrackId, Decide(source, studio, lower).SelectedLibraryTrackId);
        var result = Decide(source, studio, marked, lower);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(marked.LibraryTrackId, result.SelectedLibraryTrackId);
    }

    [Fact]
    public void SourceAlbumMarkerUsesTheConfiguredDurationTolerance()
    {
        var source = Source() with { Album = "Album (Live)" };
        var candidate = Candidate("apple-download") with { DurationMilliseconds = 244_000 };
        var scope = MatchReplayTests.Scope();
        Assert.Equal(TrackMatchReviewState.Unresolved,
            new TrackMatchDecisionEngine(new TrackMatchPolicy { DurationToleranceSeconds = 3 }).Decide(scope, source, [candidate]).State);
        Assert.Equal(TrackMatchReviewState.Accepted,
            new TrackMatchDecisionEngine(new TrackMatchPolicy { DurationToleranceSeconds = 4 }).Decide(scope, source, [candidate]).State);
    }

    [Fact]
    public void UnbracketedLocalizedLiveLabelsStillGuardAcceptance()
    {
        var source = Source() with { Isrc = "USAAA2600001" };
        var live = Candidate("apple-download") with { Album = "Album Ao Vivo", Isrc = source.Isrc };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, live).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, live with { Album = "Album", Title = "Song En Directo" }).State);
    }

    [Theory]
    [InlineData("First Mix", "Second Mix")]
    [InlineData("First Edit", "Second Edit")]
    public void DifferentNamedMixesCannotUseAnExactIdentifier(string first, string second)
    {
        var source = Source() with { Title = "Song (" + first + ")", Isrc = "USAAA2600001" };
        var candidate = Candidate("apple-download") with { Title = "Song (" + second + ")", Isrc = source.Isrc };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
    }

    [Theory]
    [InlineData("Instrumental")]
    [InlineData("Acoustic")]
    [InlineData("En Vivo")]
    [InlineData("Krono Remix")]
    public void RecordingLabelsCanAgreeAcrossTitleAndAlbum(string marker)
    {
        var source = Source() with { Album = "Album (" + marker + ")", Isrc = "USAAA2600001" };
        var candidate = Candidate("apple-download") with { Title = "Song (" + marker + ")", Isrc = source.Isrc };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
    }

    [Theory]
    [InlineData("Remastered")]
    [InlineData("2011 Remaster")]
    [InlineData("Deluxe")]
    [InlineData("Anniversary")]
    [InlineData("Bonus Track")]
    [InlineData("Expanded")]
    public void EditionLabelsAllowAcceptanceAndPreferTheSourceEdition(string edition)
    {
        var source = Source() with { Album = "Album (" + edition + ")" };
        var original = Candidate("apple-download");
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, original).State);
        var editionCandidate = original with { LibraryTrackId = Guid.NewGuid(), BackendItemId = "edition", Album = source.Album, DurationMilliseconds = 241_000 };
        Assert.Equal(editionCandidate.LibraryTrackId, Decide(source, original, editionCandidate).SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("feat.")]
    [InlineData("ft.")]
    [InlineData("featuring")]
    [InlineData("with")]
    [InlineData("&")]
    [InlineData("x")]
    [InlineData(",")]
    public void CreditSeparatorsAndTitlePunctuationKeepTheMainArtist(string separator)
    {
        var source = Source() with { Title = "Dánce, Again!", Artist = "Artist " + separator + " Guest" };
        var candidate = Candidate("apple-download") with { Title = "Dance Again (feat. Guest)", Artist = "Artist" };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate with { Artist = "Unrelated Singer" }).State);
    }

    [Theory]
    [InlineData("spotify", "spotify", false)]
    [InlineData("apple-musickit", "apple-musickit", false)]
    [InlineData("apple-musickit", " apple-download ", false)]
    [InlineData("spotify", " Spotify ", false)]
    [InlineData("listenbrainz", "spotify", false)]
    [InlineData("listenbrainz", null, false)]
    [InlineData("listenbrainz", "listenbrainz", true)]
    [InlineData("spotify", "musicbrainz", true)]
    public void SourceDurationProvenanceControlsMismatch(string provider, string? provenance, bool accepted)
    {
        var source = Source() with { ProviderId = provider, DurationMilliseconds = 255_000, DurationProvenance = provenance };
        var candidate = Candidate("apple-download") with { DurationMilliseconds = 222_000 };
        Assert.Equal(accepted, Decide(source, candidate).State == TrackMatchReviewState.Accepted);
    }

    [Theory]
    [InlineData("Song (Live)")]
    [InlineData("Song (Remix)")]
    [InlineData("Song (Instrumental)")]
    [InlineData("Song (Acoustic)")]
    [InlineData("Song (Sped Up)")]
    public void WeakDurationDoesNotBypassRecordingVersionConflicts(string title)
    {
        var source = Source() with { DurationMilliseconds = 255_000, DurationProvenance = "listenbrainz" };
        var candidate = Candidate("apple-download") with { Title = title, DurationMilliseconds = 222_000 };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
    }

    [Fact]
    public void WeakDurationOriginalWithFeatureRequiresMatchingAlbum()
    {
        var source = Source() with { Title = "Let Me Love You", Artist = "DJ Snake", Album = "Let Me Love You", DurationMilliseconds = 182_000, DurationProvenance = "listenbrainz" };
        var original = Candidate("apple-download") with { Title = "Let Me Love You (feat. Justin Bieber)", Artist = "DJ Snake", Album = "Let Me Love You - Single", DurationMilliseconds = 205_000 };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, original).State);
        Assert.Equal(TrackMatchReviewState.Suggested, Decide(source, original with { Album = "Encore" }).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source with { Isrc = "source-recording" }, original with { Isrc = "other-recording" }).State);
    }

    [Theory]
    [InlineData("Let Me Love You")]
    [InlineData("Let Me Love You - Single")]
    [InlineData("Let Me Love You (feat. Justin Bieber) - Single")]
    [InlineData("Let Me Love You (feat. Justin Bieber) - EP")]
    public void CorroboratedSingleRecordingAcceptsThePreferredAlbumCopy(string album)
    {
        var source = LoveYouSource() with { Album = album };
        var apple = LoveYouCandidate("apple-download");
        var deezer = LoveYouCandidate("deezer") with { Title = source.Title, DurationMilliseconds = 206_000 };
        var result = Decide(source, deezer, apple);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(apple.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Contains("single_album_recording_corroborated", result.Candidates[0].Reasons);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorroboratedSingleRecordingKeepsLocalPrecedenceAndFeaturedArtistEvidence(bool knownIsrc)
    {
        var local = LoveYouCandidate("local") with
        {
            IsLocal = true,
            BackendLibraryId = "music",
            ProviderTrackIds = null,
            Title = "Let Me Love You",
            Artist = "DJ Snake, Justin Bieber",
            DurationMilliseconds = 206_000,
            Isrc = knownIsrc ? "QMZSY1600015" : null
        };
        var result = Decide(LoveYouSource(), local, LoveYouCandidate("apple-download"), LoveYouCandidate("deezer"));
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(local.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(1, result.Candidates[0].Components!["primaryArtist"]);
        Assert.Equal(1, result.Candidates[0].Components!["artist"]);
        Assert.Contains("weak_source_duration", result.Reasons);
        var conflict = local with { Isrc = "OTHER1600015" };
        var fallback = LoveYouCandidate("apple-download");
        Assert.Equal(fallback.LibraryTrackId,
            Decide(LoveYouSource(), conflict, fallback, LoveYouCandidate("deezer")).SelectedLibraryTrackId);
    }

    [Fact]
    public void PositiveVocalEvidenceKeepsLocalPrecedence()
    {
        var source = Source() with
        {
            Title = "Up",
            Artist = "Cardi B",
            Album = "Up (Instrumental)",
            DurationMilliseconds = 156_946,
            DurationProvenance = "spotify",
            IsExplicit = true
        };
        var apple = Candidate("apple-download") with
        {
            Title = "Up",
            Artist = "Cardi B",
            Album = "AM I THE DRAMA?",
            DurationMilliseconds = 156_000,
            IsExplicit = true,
            Isrc = "USAT22100061"
        };
        var local = apple with
        {
            LibraryTrackId = Guid.NewGuid(),
            IsLocal = true,
            BackendLibraryId = "music",
            ProviderTrackIds = null,
            Album = "Up",
            DurationMilliseconds = 156_944
        };
        var result = Decide(source, local, apple);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(local.LibraryTrackId, result.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("conflicting")]
    [InlineData("same-provider")]
    [InlineData("local-aliases")]
    [InlineData("different-artist")]
    [InlineData("remix")]
    [InlineData("explicit")]
    public void SingleAlbumRescueRequiresIndependentQualifiedRecordingEvidence(string evidence)
    {
        var apple = LoveYouCandidate("apple-download");
        var other = LoveYouCandidate("deezer");
        other = evidence switch
        {
            "missing" => other with { Isrc = null },
            "conflicting" => other with { Isrc = "OTHER1600015" },
            "same-provider" => other with { ProviderTrackIds = apple.ProviderTrackIds, Album = "Encore (Deluxe)" },
            "local-aliases" => other with { IsLocal = true, BackendLibraryId = "music", ProviderTrackIds = new Dictionary<string, string> { ["apple-download"] = "copy", ["deezer"] = "copy" } },
            "different-artist" => other with { Artist = "Mario" },
            "remix" => other with { Title = "Let Me Love You (R3hab Remix)" },
            "explicit" => other with { IsExplicit = true },
            _ => other
        };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(LoveYouSource(), apple, other).State);
    }

    [Fact]
    public void SingleAlbumRescuePreservesSourceAlbumAndArtistRequirements()
    {
        var apple = LoveYouCandidate("apple-download");
        var deezer = LoveYouCandidate("deezer");
        Assert.NotEqual(TrackMatchReviewState.Accepted,
            Decide(LoveYouSource() with { Album = "Other Single" }, apple, deezer).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted,
            Decide(LoveYouSource(), apple with { Artist = "Mario" }, deezer with { Artist = "Mario" }).State);
    }

    private static ExternalTrackMatchSnapshot LoveYouSource() => Source() with
    {
        ProviderId = "listenbrainz",
        Title = "Let Me Love You",
        Artist = "DJ Snake",
        Album = "Let Me Love You",
        DurationMilliseconds = 182_000,
        DurationProvenance = "listenbrainz",
        MusicBrainzRecordingId = "324cf63f-6cd4-483c-a81b-5e7fadeef7db"
    };

    private static LocalTrackMatchCandidate LoveYouCandidate(string provider) => Candidate(provider) with
    {
        Title = "Let Me Love You (feat. Justin Bieber)",
        Artist = "DJ Snake",
        Album = "Encore",
        DurationMilliseconds = 205_000,
        Isrc = "QMZSY1600015"
    };

    [Theory]
    [InlineData(null)]
    [InlineData("spotify")]
    [InlineData("listenbrainz")]
    public void StoredMusicBrainzIsrcAcceptsAcrossDurationProvenance(string? provenance)
    {
        var source = LoveYouSource() with
        {
            DurationProvenance = provenance,
            MusicBrainzRecordingIsrcs = ["OTHER1600015", "QM-ZSY-16-00015"]
        };
        var apple = LoveYouCandidate("apple-download");
        var result = Decide(source, LoveYouCandidate("deezer"), apple);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(apple.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Contains("musicbrainz_recording_isrc_exact", result.Reasons);
    }

    [Fact]
    public void AlternateIsrcsStoredForTheSameMusicBrainzRecordingAreCompatible()
    {
        var source = LoveYouSource() with
        {
            Isrc = "OTHER1600015",
            DurationProvenance = "spotify",
            MusicBrainzRecordingIsrcs = ["OTHER1600015", "QMZSY1600015"]
        };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, LoveYouCandidate("apple-download")).State);
    }

    [Theory]
    [InlineData("conflicting-isrc")]
    [InlineData("different-artist")]
    [InlineData("remix")]
    public void StoredMusicBrainzEvidenceCannotAcceptConflictingRecordings(string conflict)
    {
        var source = LoveYouSource() with { MusicBrainzRecordingIsrcs = ["QMZSY1600015"] };
        var apple = LoveYouCandidate("apple-download");
        apple = conflict switch
        {
            "conflicting-isrc" => apple with { Isrc = "OTHER1600015" },
            "different-artist" => apple with { Artist = "Mario" },
            _ => apple with { Title = "Let Me Love You (R3hab Remix)" }
        };
        var deezer = LoveYouCandidate("deezer") with { Isrc = apple.Isrc, Artist = apple.Artist, Title = apple.Title };
        var result = Decide(source, apple, deezer);
        Assert.NotEqual(TrackMatchReviewState.Accepted, result.State);
        if (conflict == "conflicting-isrc")
            Assert.All(result.Candidates, score => Assert.Contains("musicbrainz_recording_isrc_conflict", score.Reasons));
    }

    [Fact]
    public void WeakDurationRequiresExactTitleAndPrimaryArtist()
    {
        var source = Source() with { DurationMilliseconds = 255_000, DurationProvenance = "listenbrainz" };
        var candidate = Candidate("apple-download") with { DurationMilliseconds = 222_000 };
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate with { Title = "Other Song" }).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate with { Artist = "Other Artist" }).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source with { Album = null }, candidate).State);
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate with { Album = source.Album + " (Deluxe Edition)" }).State);
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate with { DurationMilliseconds = null }).State);
    }

    [Fact]
    public void MainArtistOnlyMetadataCanCompareTitleSideFeatureCredits()
    {
        var source = Source();
        var candidate = Candidate("apple-download") with { Title = "Song [featuring Guest]" };
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(source, candidate).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source with { Title = "Song (feat. Other)" }, candidate).State);
        Assert.NotEqual(TrackMatchReviewState.Accepted, Decide(source, candidate with { Artist = "Unrelated Singer" }).State);
    }

    [Fact]
    public void OrdinaryTitleWordsAreNotInferredAsFeatureCredits()
    {
        Assert.Equal("song with you", allstarr.Services.Common.FuzzyMatcher.NormalizeTitleForMatching(
            "Song With You", "Artist", "Artist"));
    }

    [Fact]
    public void FeatureCreditsCanMoveBetweenTheArtistFieldAndTitle()
    {
        var source = Source() with { Title = "Lean On (feat. MØ & DJ Snake)", Artist = "Major Lazer, MØ, DJ Snake" };
        var preferred = Candidate("apple-download") with { Title = "Lean On (feat. MØ)", Artist = "Major Lazer & DJ Snake" };
        var lower = Candidate("deezer") with { Title = "Lean On", Artist = "Major Lazer" };
        var decision = Decide(source, preferred, lower);
        Assert.Equal(TrackMatchReviewState.Accepted, decision.State);
        Assert.Equal(preferred.LibraryTrackId, decision.SelectedLibraryTrackId);
    }

    [Fact]
    public void SameProviderCandidatesWithoutDistinguishingFactsRemainAmbiguous()
    {
        var source = Source() with { Album = null, DurationMilliseconds = null, IsExplicit = null };
        var first = Candidate("deezer") with { Album = null, DurationMilliseconds = null, IsExplicit = null };
        var second = first with { LibraryTrackId = Guid.NewGuid(), BackendItemId = "second" };
        var result = Decide(source, first, second);
        Assert.Equal(TrackMatchReviewState.Ambiguous, result.State);
        Assert.Null(result.SelectedLibraryTrackId);
        Assert.True(result.RequiresReview);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal(0.94, candidate.Confidence, 4));
        var preferred = first with { ProviderTrackIds = new Dictionary<string, string> { ["apple-download"] = "preferred" } };
        var acrossProviders = Decide(source, second, preferred);
        Assert.Equal(TrackMatchReviewState.Accepted, acrossProviders.State);
        Assert.Equal(preferred.LibraryTrackId, acrossProviders.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameProviderTieNeedsSourceEditionOrDurationEvidence(bool durationKnown)
    {
        var source = Source() with { IsExplicit = null };
        var supported = Candidate("deezer") with
        {
            Album = durationKnown ? null : source.Album,
            DurationMilliseconds = durationKnown ? 240_000 : null,
            IsExplicit = null
        };
        var unknown = supported with
        {
            LibraryTrackId = Guid.NewGuid(),
            BackendItemId = "unknown",
            Album = null,
            DurationMilliseconds = null
        };
        var result = Decide(source, unknown, supported);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(supported.LibraryTrackId, result.SelectedLibraryTrackId);
        Assert.Equal(TrackMatchReviewState.Ambiguous,
            Decide(source with { Album = null, DurationMilliseconds = null }, unknown, supported).State);
    }

    [Fact]
    public void DuplicateEditionsUseProviderOrderAfterEditionAndScore()
    {
        var first = Candidate("apple-download");
        var second = first with { LibraryTrackId = Guid.NewGuid(), BackendItemId = "second" };
        Assert.Equal(first.LibraryTrackId, Decide(Source(), first, second).SelectedLibraryTrackId);
        Assert.Equal(second.LibraryTrackId, Decide(Source(), second, first).SelectedLibraryTrackId);
        Assert.Equal(TrackMatchReviewState.Accepted, Decide(Source(), first, second).State);
    }

    [Fact]
    public void UnknownSourceExplicitnessUsesAlbumBeforeExplicitPreference()
    {
        var source = Source() with { IsExplicit = null };
        var clean = Candidate("apple-download") with { IsExplicit = false };
        var explicitEdition = clean with { LibraryTrackId = Guid.NewGuid(), BackendItemId = "explicit", IsExplicit = true, Album = "Other" };
        Assert.Equal(clean.LibraryTrackId, Decide(source, explicitEdition, clean).SelectedLibraryTrackId);
        Assert.Equal(explicitEdition.LibraryTrackId, Decide(source with { Album = null }, clean, explicitEdition).SelectedLibraryTrackId);
        var lowerProviderEdition = Candidate("deezer");
        Assert.Equal(explicitEdition.LibraryTrackId, Decide(source, clean with { Album = "Other" }, explicitEdition, lowerProviderEdition).SelectedLibraryTrackId);
    }

    [Fact]
    public void OppositeFlagsInTheSameSourceEditionDefaultToExplicitWhenSourceIsUnknown()
    {
        var clean = Candidate("apple-download");
        var explicitCopy = clean with { LibraryTrackId = Guid.NewGuid(), BackendItemId = "explicit", IsExplicit = true };
        var result = Decide(Source() with { IsExplicit = null }, clean, explicitCopy);
        Assert.Equal(TrackMatchReviewState.Accepted, result.State);
        Assert.Equal(explicitCopy.LibraryTrackId, result.SelectedLibraryTrackId);
    }

    [Theory]
    [InlineData("{\"IsExplicit\":true}", true)]
    [InlineData("{\"isExplicit\":false}", false)]
    [InlineData("{\"ExplicitContentLyrics\":1}", true)]
    [InlineData("{\"explicitContentLyrics\":0}", false)]
    [InlineData("{\"ExplicitContentLyrics\":3}", false)]
    [InlineData("{\"ExplicitContentLyrics\":2}", null)]
    [InlineData("{}", null)]
    [InlineData("{\"IsExplicit\":null}", null)]
    public void SnapshotExplicitnessReadsBothStoredForms(string json, bool? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, TrackSnapshotMetadata.IsExplicit(document.RootElement));
    }

    [Fact]
    public void PersistedEvidenceRetainsKnownAndUnknownInputsAndTruncation()
    {
        var source = Source() with { DurationProvenance = "spotify", ExactProviderAliases = new Dictionary<string, string> { ["deezer"] = "source-alias" } };
        var local = Candidate("local") with { IsLocal = true, BackendLibraryId = "music", CanonicalRecordingId = Guid.NewGuid(), MusicBrainzRecordingId = Guid.NewGuid().ToString(), IsExplicit = null };
        var others = Enumerable.Range(0, 21).Select(index => Candidate("apple-download") with { BackendItemId = "copy-" + index, IsExplicit = index % 2 == 0 });
        var decision = Decide(source, [local, .. others]);
        var input = MatchDecisionInput.FromDecision(Guid.NewGuid(), null, decision, 1, 1, 1, "test");
        var saved = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(input.CandidateResultsJson)!;
        Assert.Equal(20, saved.Length);
        Assert.True(saved[0].CandidatesTruncated);
        Assert.Equal(source, saved[0].Source! with { ExactProviderAliases = source.ExactProviderAliases });
        Assert.Equal(new[] { "apple-download", "deezer" }, saved[0].ProviderPriority);
        Assert.NotNull(saved[0].Policy);
        var retainedLocal = saved.Single(item => item.LibraryTrackId == local.LibraryTrackId);
        Assert.Null(retainedLocal.IsExplicit);
        Assert.Equal(local.CanonicalRecordingId, retainedLocal.CanonicalRecordingId);
        Assert.Equal(local.MusicBrainzRecordingId, retainedLocal.MusicBrainzRecordingId);
        Assert.Contains(saved, item => item.IsExplicit == true);
        Assert.Contains(saved, item => item.IsExplicit == false);
    }

    private static TrackMatchDecision Decide(ExternalTrackMatchSnapshot source, params LocalTrackMatchCandidate[] candidates) =>
        new TrackMatchDecisionEngine().Decide(new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "backend", null, 1, 1, new HashSet<string> { "music" }), source, candidates, providerPriority: ["apple-download", "deezer"]);

    private static ExternalTrackMatchSnapshot Source() => new("source", "spotify", "source", "Song", "Artist", "Album", null, 240_000, null, null, false);

    private static LocalTrackMatchCandidate Candidate(string provider) => new(Guid.NewGuid(), null, "backend", null, "track", null, "Song", "Artist", "Album", null, 240_000, null, null, false, new Dictionary<string, string> { [provider] = "track" }, IsLocal: false);
}
