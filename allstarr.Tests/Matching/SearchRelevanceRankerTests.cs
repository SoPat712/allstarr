using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class SearchRelevanceRankerTests
{
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
