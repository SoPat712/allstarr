using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class SearchRelevanceRankerTests
{
    [Theory]
    [InlineData("Get Lucky", "Get Lucky", "Daft Punk", 100)]
    [InlineData("Get Lucky Daft Punk", "Get Lucky", "Daft Punk", 130)]
    [InlineData("Daft Punk Get Lucky", "Get Lucky", "Daft Punk", 130)]
    [InlineData("Live Forever", "Live Forever", null, 100)]
    public void CancellableScoringKeepsOrdinaryFixtureScores(string query, string title, string? artist, double expected)
    {
        using var stop = new CancellationTokenSource();
        Assert.Equal(expected, SearchRelevanceRanker.Score(query, title, artist, cancellationToken: stop.Token));
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong(query, title, artist, durationSeconds: 240, cancellationToken: stop.Token));
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong(query,
            new allstarr.Models.Domain.Song { Title = title, Artist = artist ?? "", Duration = 240 }, cancellationToken: stop.Token));
        Assert.Equal(expected, SearchRelevanceRanker.ScoreAlbum(query,
            new allstarr.Models.Domain.Album { Title = title, Artist = artist ?? "" }, cancellationToken: stop.Token));
    }

    [Fact]
    public void SearchEditDistanceUsesOnlyTheNormalizedPrefixAndGeneralMatchingKeepsTheFullText()
    {
        var query = new string('a', 100) + new string('x', 100);
        var title = new string('a', 100) + new string('y', 100);
        Assert.Equal(37, FuzzyMatcher.CalculateSimilarityAggressive(query, title));
        Assert.Equal(75, FuzzyMatcher.CalculateSearchSimilarityAggressive(query, title));
        Assert.Equal(75, SearchRelevanceRanker.Score(query, title));
        Assert.Equal(95, SearchRelevanceRanker.Score("needle", new string('z', 4000) + " needle"));
    }

    [Fact]
    public void CancelledScoringStopsBeforeInspectingLongMetadata()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var title = new string('x', 100_000);
        Assert.Throws<OperationCanceledException>(() => SearchRelevanceRanker.Score("query", title, cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => SearchRelevanceRanker.ScoreSong("query", title, "artist", cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => SearchRelevanceRanker.ScoreSong("query",
            new allstarr.Models.Domain.Song { Title = title, Isrc = title }, cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => SearchRelevanceRanker.ScoreAlbum("query",
            new allstarr.Models.Domain.Album { Title = title }, cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => FuzzyMatcher.CalculateSearchSimilarityAggressive("query", title, stop.Token));
    }

    [Theory]
    [InlineData("Get Lucky Daft Punk")]
    [InlineData("Daft Punk Get Lucky")]
    [InlineData("Get Lucky")]
    public void GenuineAlternateTitleImprovesFitWithoutReplacingTheDisplayedTitle(string query)
    {
        var song = System.Text.Json.JsonSerializer.Deserialize<allstarr.Models.Domain.Song>("""
            {"Title":"Renamed [V1]","Artist":"Daft Punk","Duration":156,"AlternateTitles":["Get Lucky"]}
            """)!;

        Assert.Equal(SearchRelevanceRanker.ScoreSong(query, "Get Lucky", "Daft Punk", durationSeconds: 156),
            SearchRelevanceRanker.ScoreSong(query, song));
        Assert.Equal("Renamed [V1]", song.Title);
        Assert.True(SearchRelevanceRanker.ScoreSong(query, song) >
            SearchRelevanceRanker.ScoreSong(query, song.Title, song.Artist, durationSeconds: song.Duration));
    }

    [Theory]
    [InlineData("Live", null)]
    [InlineData("Other Artist Remix", null)]
    [InlineData("Music Box Version", null)]
    [InlineData("Instrumental", null)]
    [InlineData("Mixed", null)]
    [InlineData("", "Continuous Mix")]
    public void AlternateTitleRetainsTheRealRecordingPenalties(string label, string? album)
    {
        var title = label.Length == 0 ? "Renamed" : $"Renamed ({label})";
        var song = System.Text.Json.JsonSerializer.Deserialize<allstarr.Models.Domain.Song>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Title = title,
                Artist = "Daft Punk",
                Album = album,
                Duration = 240,
                AlternateTitles = new[] { "Get Lucky" }
            }))!;
        var expectedTitle = label.Length == 0 ? "Get Lucky" : $"Get Lucky ({label})";
        var expected = SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", expectedTitle, "Daft Punk", album,
            durationSeconds: 240);

        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song));
        Assert.True(expected < SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", "Get Lucky", "Daft Punk",
            durationSeconds: 240));
    }

    [Theory]
    [InlineData(15, false, false)]
    [InlineData(null, true, false)]
    [InlineData(240, false, true)]
    public void AlternateTitleRetainsActualDurationAndRecordingEvidence(int? duration, bool knownDuration, bool proven)
    {
        var song = new allstarr.Models.Domain.Song
        {
            Title = "Renamed",
            Artist = "Daft Punk",
            Duration = duration,
            AlternateTitles = ["Get Lucky"],
            Isrc = proven ? "USAA12345678" : null
        };
        Assert.Equal(SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", "Get Lucky", "Daft Punk",
                durationSeconds: duration, provenRecording: proven, hasKnownDurationVersion: knownDuration),
            SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song, knownDuration));
        Assert.Equal(SearchRelevanceRanker.RecordingTitle("Renamed", "Daft Punk"),
            SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist));
    }

    [Fact]
    public void MissingInvalidExcessAndLocalAlternateTitlesDoNotAffectRelevance()
    {
        var song = new allstarr.Models.Domain.Song { Title = "Renamed", Artist = "Daft Punk", Duration = 240 };
        var expected = SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song.Title, song.Artist, durationSeconds: 240);
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song));
        song.AlternateTitles = null!;
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song));
        song.AlternateTitles = ["", "Untrimmed ", "Control\u0001Alias", new string('a', 501), null!, "\n", "\t", "   ", "Get Lucky"];
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song));
        song.AlternateTitles = ["Get Lucky"];
        song.IsLocal = true;
        Assert.Equal(expected, SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song));
    }

    [Fact]
    public void AnAliasDoesNotSupplyAnotherArtistsCreditsOrEraseRequestedVersionIntent()
    {
        var song = new allstarr.Models.Domain.Song
        {
            Title = "Renamed (Other Artist Remix)",
            Artist = "Someone Else",
            Duration = 240,
            AlternateTitles = ["Get Lucky Daft Punk"]
        };
        Assert.True(SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", song) <
            SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk", "Get Lucky", "Daft Punk", durationSeconds: 240));
        song.Artist = "Daft Punk";
        song.AlternateTitles = ["Get Lucky"];
        var requested = SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk Other Artist Remix", song);
        song.Title = "Renamed";
        Assert.True(requested > SearchRelevanceRanker.ScoreSong("Get Lucky Daft Punk Other Artist Remix", song));
    }

    [Theory]
    [InlineData("Get Lucky (Daft Punk)", "Charlie Parra Del Riego")]
    [InlineData("Daft Punk - Get Lucky", "Various Artists")]
    [InlineData("Get Lucky - Daft Punk", "Teenage Kicks.")]
    [InlineData("Get Lucky", "Daft Punk Experience")]
    public void MainArtistMatchOutranksArtistTokensInTheTitleOrALongerArtistName(string title, string artist)
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky Daft Punk", title, artist));
    }

    [Theory]
    [InlineData("Originally by Daft Punk")]
    [InlineData("Originally performed by Daft Punk")]
    [InlineData("In the style of Daft Punk")]
    [InlineData("Made famous by Daft Punk")]
    [InlineData("Lullaby Version")]
    [InlineData("Music Box Version")]
    [InlineData("Bossa Nova Version")]
    [InlineData("8-bit Version")]
    [InlineData("8-bit")]
    [InlineData("Piano Cover")]
    public void SharedClassifiersRecognizeDerivativeRecordings(string label)
    {
        Assert.True(FuzzyMatcher.HasCoverRecordingLabel(label) || FuzzyMatcher.BackingRecordingTags(label).Count > 0);
    }

    [Theory]
    [InlineData("Drumless Edition")]
    [InlineData("Instrumental")]
    [InlineData("Acapella")]
    [InlineData("A cappella")]
    [InlineData("Karaoke")]
    [InlineData("Sped Up")]
    [InlineData("Slowed + Reverb")]
    [InlineData("8D")]
    [InlineData("Nightcore")]
    [InlineData("Extended Mix")]
    [InlineData("Extended Version")]
    [InlineData("Club Mix")]
    [InlineData("Other Artist Remix")]
    public void SpecialEditionsRankBelowGenuineVersionsUnlessRequested(string label)
    {
        var title = $"Get Lucky ({label}) (feat. Pharrell Williams and Nile Rodgers)";
        Assert.NotEmpty(FuzzyMatcher.SemanticVersionTags(title));
        Assert.True(SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky (Radio Edit)", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky Daft Punk", title, "Daft Punk"));
        Assert.True(SearchRelevanceRanker.Score($"Get Lucky Daft Punk {label}", title, "Daft Punk") >
                    SearchRelevanceRanker.Score($"Get Lucky Daft Punk {label}", "Get Lucky", "Daft Punk"));
    }

    [Theory]
    [InlineData("Remastered")]
    [InlineData("2013 Remaster")]
    [InlineData("Daft Punk Remix")]
    public void GenuineAnnotationsDoNotReceiveSpecialEditionPenalties(string label)
    {
        Assert.Equal(SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky", "Daft Punk"),
            SearchRelevanceRanker.Score("Get Lucky Daft Punk", $"Get Lucky ({label})", "Daft Punk"));
    }

    [Fact]
    public void OriginalAlbumVersionOutranksRadioEditUnlessRequested()
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucky", "Get Lucky", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky", "Get Lucky (Radio Edit)", "Daft Punk"));
        Assert.True(SearchRelevanceRanker.Score("Get Lucky radio edit", "Get Lucky (Radio Edit)", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky radio edit", "Get Lucky", "Daft Punk"));
    }

    [Theory]
    [InlineData("Album Version")]
    [InlineData("Original Mix")]
    public void OriginalReleaseLabelsKeepThePreferenceOverRadioEdits(string label)
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucky Daft Punk", $"Get Lucky ({label})", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky (Radio Edit)", "Daft Punk"));
    }

    [Theory]
    [InlineData("Music Box Version")]
    [InlineData("Lullaby Version")]
    [InlineData("Piano Cover")]
    public void DerivativeLabelsRemainBelowGenuineVersionsEvenWithMatchingArtistCredits(string label)
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky (Radio Edit)", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky Daft Punk", $"Get Lucky ({label})", "Daft Punk"));
    }

    [Theory]
    [InlineData("Daft Punk Get Lucky")]
    [InlineData("Get Lucky")]
    public void OriginalRecordingOutranksAnUnrequestedUploadVariant(string query)
    {
        Assert.True(SearchRelevanceRanker.Score(query, "Get Lucky", "Daft Punk") >
                    SearchRelevanceRanker.Score(query,
                        "Daft Punk - Get Lucky #nightcore #bassboosted", "999333"));
    }

    [Theory]
    [InlineData("Mixed")]
    [InlineData("DJ Mix")]
    public void MixQueriesKeepTheRequestedMixVisible(string label)
    {
        Assert.True(SearchRelevanceRanker.Score($"Espresso {label}", $"Espresso ({label})", "Sabrina Carpenter") >
                    SearchRelevanceRanker.Score($"Espresso {label}", "Espresso", "Sabrina Carpenter"));
    }

    [Fact]
    public void ArtistAndTitleAreMatchedTogetherRegardlessOfQueryOrder()
    {
        var expected = SearchRelevanceRanker.Score("Get Lucky Daft Punk", "Get Lucky", "Daft Punk");
        Assert.Equal(expected, SearchRelevanceRanker.Score("Daft Punk Get Lucky", "Get Lucky", "Daft Punk"));
        Assert.True(expected > SearchRelevanceRanker.Score("Daft Punk Get Lucky", "Get Lucky", "Someone Else"));
    }

    [Fact]
    public void AdditionalCreditsKeepArtistContextStrongerThanATitleOnlyMatch()
    {
        var credited = SearchRelevanceRanker.Score("Daft Punk Get Lucky", "Get Lucky",
            "Daft Punk, Pharrell Williams, Nile Rodgers");
        Assert.True(credited > SearchRelevanceRanker.Score("Daft Punk Get Lucky", "Get Lucky"));
        Assert.True(credited > SearchRelevanceRanker.Score("Daft Punk Get Lucky", "Get Lucky", "Someone Else"));
    }

    [Fact]
    public void PartialTokenOverlapStillAllowsFuzzyTitleMatching()
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucki", "Get Lucky") >
                    SearchRelevanceRanker.Score("Get Lucki", "Get Ready"));
    }

    [Theory]
    [InlineData("Nightcore")]
    [InlineData("Bass Boosted")]
    [InlineData("Sped Up")]
    [InlineData("Slowed + Reverb")]
    [InlineData("Remix")]
    [InlineData("Live")]
    [InlineData("Cover")]
    [InlineData("8D")]
    [InlineData("Tribute")]
    [InlineData("Karaoke")]
    public void ExplicitVariantQueryPrefersTheRequestedVersion(string variant)
    {
        var title = $"Get Lucky ({variant})";
        Assert.True(SearchRelevanceRanker.Score($"Get Lucky {variant}", title, "Daft Punk") >
                    SearchRelevanceRanker.Score($"Get Lucky {variant}", "Get Lucky", "Daft Punk"));
        Assert.True(SearchRelevanceRanker.Score("Get Lucky", "Get Lucky", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky", title, "Daft Punk"));
    }

    [Theory]
    [InlineData("Live Forever")]
    [InlineData("Cover Me")]
    [InlineData("Long Live")]
    public void OrdinaryTitleWordsDoNotBecomeVersionPenalties(string title)
    {
        Assert.Equal(100, SearchRelevanceRanker.Score(title, title));
    }

    [Theory]
    [InlineData("Karaoke Get Lucky")]
    [InlineData("Get Lucky - Tribute to Daft Punk")]
    [InlineData("Get Lucky 8D Audio")]
    public void UploadVersionLabelsDoNotRequireBrackets(string title)
    {
        Assert.True(SearchRelevanceRanker.Score("Get Lucky", "Get Lucky", "Daft Punk") >
                    SearchRelevanceRanker.Score("Get Lucky", title, "Daft Punk"));
    }
}
