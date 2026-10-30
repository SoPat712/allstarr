using Xunit;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("Anxiety - Single", "anxiety")]
    [InlineData("Anxiety - EP", "anxiety")]
    [InlineData("Anxiety (sInGlE)", "anxiety")]
    [InlineData("Anxiety [Single]", "anxiety")]
    [InlineData("Anxiety (EP)", "anxiety")]
    [InlineData("Anxiety [e.p.]", "anxiety")]
    [InlineData("Anxiety – Single  ", "anxiety")]
    [InlineData("Anxiety (Deluxe)", "anxiety deluxe")]
    [InlineData("Anxiety (Extended)", "anxiety extended")]
    [InlineData("Anxiety (Remastered)", "anxiety remastered")]
    [InlineData("Anxiety (Live)", "anxiety live")]
    [InlineData("Anxiety - Single Collection", "anxiety single collection")]
    [InlineData("Anxiety (Single) (Deluxe)", "anxiety single deluxe")]
    [InlineData("Single", "single")]
    [InlineData(null, "")]
    public void AlbumNormalizationOnlyStripsTrailingStoreFormats(string? album, string expected)
    {
        Assert.Equal(expected, FuzzyMatcher.NormalizeAlbumForMatching(album));
    }

    [Theory]
    [InlineData("Anxiety", "Anxiety - Single", true)]
    [InlineData("Anxiety", "Anxiety - EP", true)]
    [InlineData("Anxiety", "Anxiety (Deluxe)", true)]
    [InlineData("Anxiety", "Anxiety (Demo)", false)]
    [InlineData("Anxiety", "Anxiety (Remastered)", true)]
    public void AlbumRecordingAnnotationsKeepVersionsSeparate(string source, string candidate, bool expected)
    {
        Assert.Equal(expected, FuzzyMatcher.RecordingVersionsMatch("Anxiety", source, "Anxiety", candidate));
    }

    [Fact]
    public void VocalSourceAlbumMarkersCanBeIgnoredOnlyWhenTheyAreAlbumOnly()
    {
        Assert.True(FuzzyMatcher.RecordingVersionsMatch(
            "Track", "Album (Instrumental)", "Track", "Album",
            allowVocalSourceAlbumMarkers: true));
        Assert.False(FuzzyMatcher.RecordingVersionsMatch(
            "Track (Instrumental)", "Album", "Track", "Album",
            allowVocalSourceAlbumMarkers: true));
        Assert.False(FuzzyMatcher.RecordingVersionsMatch(
            "Track", "Album (A cappella)", "Track", "Album",
            allowVocalSourceAlbumMarkers: true));
    }

    [Fact]
    public void VocalSourceAlbumMarkerConfirmationRetainsACappellaAndIgnoresAlbumOnlyInstrumental()
    {
        Assert.False(FuzzyMatcher.SourceAlbumRequiresConfirmation(
            "Track", "Album (Instrumental)", "Track", "Album",
            allowSourceAlbumOnlyMarkers: false, allowVocalSourceAlbumMarkers: true));
        Assert.True(FuzzyMatcher.SourceAlbumRequiresConfirmation(
            "Track", "Album (A cappella)", "Track", "Album",
            allowSourceAlbumOnlyMarkers: false, allowVocalSourceAlbumMarkers: true));
    }

    [Theory]
    [InlineData("⭐ Song")]
    [InlineData("⭐Song")]
    public void BestVersionPrefixIsRemovedFromTitleQueriesAndNormalization(string title)
    {
        Assert.Equal("Song", FuzzyMatcher.StripDecorators(title));
        Assert.Equal("Song", FuzzyMatcher.StripDecorators(title, "Artist"));
        Assert.Equal("Song", FuzzyMatcher.SearchQuery(title));
        Assert.Equal("song", FuzzyMatcher.NormalizeTitleForMatching(title, "Artist"));
        Assert.Equal(100, FuzzyMatcher.CalculateSimilarity(title, "Song"));
    }

    [Theory]
    [InlineData("Daft Punk Get Lucky")]
    [InlineData("Get Lucky Daft Punk")]
    [InlineData("Get Lucky Daft Punk, Pharrell Williams, Nile Rodgers")]
    public void StripDecorators_DoesNotTreatPartOfAnArtistNameAsAFeaturedCredit(string title)
    {
        Assert.Equal(title, FuzzyMatcher.StripDecorators(title));
    }

    [Fact]
    public void Artist_aware_search_uses_the_primary_credit()
    {
        Assert.Equal(
            "Crush Selena Gomez & The Scene",
            FuzzyMatcher.SearchQuery("Crush", "Selena Gomez & The Scene"));
        Assert.Equal(
            "Feels Calvin Harris",
            FuzzyMatcher.SearchQuery("Feels", "Calvin Harris, Pharrell Williams"));
    }

    [Theory]
    [InlineData("Link Up (Metro Boomin & Don Toliver, Wizkid feat. BEAM & Toian) - Spider-Verse Remix (Spider-Man: Across the Spider-Verse)", "Link Up")]
    [InlineData("Feels", "Feels")]
    public void SearchQuery_UsesTheBaseTitle(string title, string expected)
    {
        Assert.Equal(expected, FuzzyMatcher.SearchQuery(title));
    }

    [Theory]
    [InlineData("Mr. Brightside", "Mr. Brightside", 100)]
    [InlineData("Mr Brightside", "Mr. Brightside", 100)]
    [InlineData("Mr. Brightside", "Mr Brightside", 100)]
    [InlineData("The Killers", "Killers", 85)]
    [InlineData("Dua Lipa", "Dua-Lipa", 100)]
    public void CalculateSimilarity_ExactAndNearMatches_ReturnsHighScore(string str1, string str2, int expectedMin)
    {
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= expectedMin, $"Expected score >= {expectedMin}, got {score}");
    }

    [Theory]
    [InlineData("Mr. Brightside", "Somebody Told Me", 20)]
    [InlineData("The Killers", "The Beatles", 40)]
    [InlineData("Hot Fuss", "Sam's Town", 20)]
    public void CalculateSimilarity_DifferentStrings_ReturnsLowScore(string str1, string str2, int expectedMax)
    {
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score <= expectedMax, $"Expected score <= {expectedMax}, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IgnoresPunctuation()
    {
        var str1 = "Don't Stop Believin'";
        var str2 = "Dont Stop Believin";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 95, $"Expected high score for punctuation differences, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IgnoresCase()
    {
        var str1 = "Mr. Brightside";
        var str2 = "mr. brightside";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.Equal(100, score);
    }

    [Fact]
    public void CalculateSimilarity_HandlesArticles()
    {
        var str1 = "The Killers";
        var str2 = "Killers";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 80, $"Expected high score when 'The' is removed, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_HandlesFeaturedArtists()
    {
        var str1 = "Song Title (feat. Artist)";
        var str2 = "Song Title";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 70, $"Expected decent score for featured artist variations, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_HandlesRemixes()
    {
        var str1 = "Song Title - Radio Edit";
        var str2 = "Song Title";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 70, $"Expected decent score for remix/edit variations, got {score}");
    }

    [Theory]
    [InlineData("Kesha", "Ke$ha")]
    [InlineData("PILLOWTALK", "PiLlOwT4lK")]
    [InlineData("PILLOWTALK", "P1ll0wtalk")]
    [InlineData("Heebiejeebies - Bonus", "Heebiejeebies")]
    [InlineData("Homemade Dynamite (Feat. Khalid, Post Malone & SZA) - REMIX", "Homemade Dynamite (REMIX)")]
    [InlineData("Song Title - Bonus Track", "Song Title")]
    [InlineData("Song Title - Deluxe Edition", "Song Title")]
    [InlineData("Song Title (Deluxe Edition)", "Song Title")]
    [InlineData("Song Title [Bonus Track]", "Song Title")]
    [InlineData("[Bonus Track] Song Title", "Song Title")]
    [InlineData("Song Title (Album Version)", "Song Title")]
    public void CalculateSimilarityAggressive_NormalizesSafeDecoratorsAndStylization(string source, string candidate)
    {
        Assert.Equal(100, FuzzyMatcher.CalculateSimilarityAggressive(source, candidate));
    }

    [Theory]
    [InlineData("Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne [ei8ht and OFFSET] - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT x Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT, Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT feat. Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT ft. Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT with Offset) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne - EI8HT & Offset - Spider-Verse Remix")]
    [InlineData("Silk & Cologne - EI8HT & Offset (Spider-Verse remix)")]
    public void NormalizeTitleForMatchingUsesOnlyTheTracksOwnCredits(string title)
    {
        Assert.Equal(
            FuzzyMatcher.NormalizeTitleForMatching("Silk and Cologne (Spider-Verse remix)", "Ei8ht, OFFSET"),
            FuzzyMatcher.NormalizeTitleForMatching(title, "EI8HT, Offset"));
    }

    [Theory]
    [InlineData("Silk & Cologne - EI8HT & Offset - Spider-Verse Remix")]
    [InlineData("Silk & Cologne - EI8HT & Offset (Spider-Verse remix)")]
    public void SemanticVersionTagsUseTheSeparateRemixSegment(string title)
    {
        Assert.True(FuzzyMatcher.SemanticVersionTags("Silk and Cologne (Spider-Verse remix)")
            .SetEquals(FuzzyMatcher.SemanticVersionTags(title)));
    }

    [Theory]
    [InlineData("Song (First Producer - Remix)", "Song (Second Producer - Remix)")]
    [InlineData("Song - First Producer Remix", "Song - Second Producer Remix")]
    public void SemanticVersionTagsPreserveDifferentRemixers(string source, string candidate)
    {
        Assert.False(FuzzyMatcher.SemanticVersionTags(source)
            .SetEquals(FuzzyMatcher.SemanticVersionTags(candidate)));
    }

    [Theory]
    [InlineData("Silk & Cologne (Other Artist) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (EI8HT & Other Artist) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne (feat. Other Artist) - Spider-Verse Remix")]
    [InlineData("Silk & Cologne - Other Artist - Spider-Verse Remix")]
    public void NormalizeTitleForMatchingPreservesUncreditedArtists(string title)
    {
        Assert.NotEqual(
            FuzzyMatcher.NormalizeTitleForMatching("Silk and Cologne (Spider-Verse remix)", "EI8HT, Offset"),
            FuzzyMatcher.NormalizeTitleForMatching(title, "EI8HT, Offset"));
    }

    [Theory]
    [InlineData("Silk and Cologne")]
    [InlineData("Silk and Cologne (Other Producer remix)")]
    [InlineData("Silk and Cologne (Live)")]
    [InlineData("Silk and Cologne (Acoustic)")]
    [InlineData("Silk and Cologne (Sped Up)")]
    [InlineData("Silk and Cologne (Slowed)")]
    [InlineData("Silk and Cologne (Instrumental)")]
    [InlineData("Silk and Cologne (Karaoke)")]
    public void NormalizeTitleForMatchingPreservesVersionLabels(string title)
    {
        Assert.NotEqual(
            FuzzyMatcher.NormalizeTitleForMatching("Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix", "EI8HT, Offset"),
            FuzzyMatcher.NormalizeTitleForMatching(title, "EI8HT, Offset"));
    }

    [Theory]
    [InlineData("Song (Other Artist) - Remix", "Song - Other Artist - Remix")]
    [InlineData("Song (Other Artist) - Remix", "Song Other Artist (Remix)")]
    [InlineData("Song (Official Audio)", "Song")]
    [InlineData("Song (Alpha & Beta) - Remix", "Song (Remix)")]
    public void TitleAnnotationsMatchAllowsEquivalentAndBenignDecorators(string source, string candidate)
    {
        Assert.True(FuzzyMatcher.TitleAnnotationsMatch(source, "Alpha, Beta", candidate, "Alpha, Beta"));
    }

    [Theory]
    [InlineData("Let Me Hit It - Audiostalkers Mix", "Sporty-O", "Let Me Hit It (Audiostalkers Mix)", "Sporty-O")]
    [InlineData("Enemy (with JID) - from the series Arcane League of Legends", "Imagine Dragons", "Enemy (From the animated series \"Arcane League of Legends\")", "Imagine Dragons, Arcane & League of Legends Music")]
    public void EquivalentTitleAnnotationsMatch(string source, string artist, string candidate, string candidateArtist)
    {
        Assert.True(FuzzyMatcher.TitleAnnotationsMatch(source, artist, candidate, candidateArtist));
        Assert.Equal(FuzzyMatcher.NormalizeTitleForMatching(source, artist, candidateArtist),
            FuzzyMatcher.NormalizeTitleForMatching(candidate, candidateArtist, artist));
    }

    [Theory]
    [InlineData("Magic - From \"Hexed\"", "Magic (From \"Hexed\")")]
    [InlineData("Magic – From “Hexed”", "Magic [from ‘Hexed’]")]
    [InlineData("Magic - From the film \"Hexed\"", "Magic (From the movie ‘Hexed’)")]
    [InlineData("Magic - From the series \"Hexed\"", "Magic (From Hexed)")]
    [InlineData("Magic - From \"The Hexed\"", "Magic (From The Hexed)")]
    public void EquivalentFromAnnotationsMatchAcrossDashBracketAndDescriptionForms(
        string source, string candidate)
    {
        Assert.True(FuzzyMatcher.TitleAnnotationsMatch(
            source, "Hailee Steinfeld", candidate, "Hailee Steinfeld"));
        Assert.Equal(
            FuzzyMatcher.NormalizeTitleForMatching(source, "Hailee Steinfeld"),
            FuzzyMatcher.NormalizeTitleForMatching(candidate, "Hailee Steinfeld"));
    }

    [Theory]
    [InlineData(
        "Draw You Out - From The Hunger Games: Sunrise on the Reaping - The Album",
        "Draw You Out (From The Hunger Games: Sunrise on the Reaping - The Album)")]
    [InlineData(
        "Back To You - From 13 Reasons Why – Season 2 Soundtrack",
        "Back To You (From 13 Reasons Why – Season 2 Soundtrack)")]
    public void EquivalentFromAnnotationsKeepInternalDashDescriptors(
        string source, string candidate)
    {
        Assert.True(FuzzyMatcher.TitleAnnotationsMatch(
            source, "Artist", candidate, "Artist"));
        Assert.Equal(
            FuzzyMatcher.NormalizeTitleForMatching(source, "Artist"),
            FuzzyMatcher.NormalizeTitleForMatching(candidate, "Artist"));
    }

    [Fact]
    public void ConflictingFromFilmDescriptorsRemainDistinctWithInternalDashes()
    {
        Assert.False(FuzzyMatcher.TitleAnnotationsMatch(
            "Draw You Out - From The Hunger Games: Sunrise on the Reaping - The Album",
            "Artist",
            "Draw You Out (From Divergent: Original Motion Picture Soundtrack - The Album)",
            "Artist"));
    }

    [Theory]
    [InlineData("Magic - From \"Hexed\"", "Magic (From \"Wicked\")")]
    [InlineData("Magic - From the film \"Hexed\"", "Magic (From the series \"Wicked\")")]
    public void ConflictingFromAnnotationsRemainDistinct(string source, string candidate)
    {
        Assert.False(FuzzyMatcher.TitleAnnotationsMatch(
            source, "Hailee Steinfeld", candidate, "Hailee Steinfeld"));
    }

    [Theory]
    [InlineData("From X")]
    [InlineData("From the film X")]
    [InlineData("From the series X")]
    public void OrdinaryFromTitlesKeepTheirWords(string title)
    {
        Assert.Equal(title, FuzzyMatcher.StripDecorators(title));
        Assert.Equal(
            FuzzyMatcher.NormalizeForMatching(title),
            FuzzyMatcher.NormalizeTitleForMatching(title, "Artist"));
    }

    [Fact]
    public void OrdinaryWithTitleKeepsItsWords()
    {
        Assert.Equal("with you", FuzzyMatcher.NormalizeTitleForMatching("With You", "Chris Brown"));
    }

    [Theory]
    [InlineData("With You", "You")]
    [InlineData("Feat You", "You")]
    [InlineData("Featuring You", "You")]
    [InlineData("Ft You", "You")]
    public void LeadingCreditWordsRemainPartOfTheTitle(string title, string shorter)
    {
        Assert.Equal(FuzzyMatcher.NormalizeForMatching(title), FuzzyMatcher.NormalizeTitleForMatching(title, "Artist", "Artist"));
        Assert.Equal(title, FuzzyMatcher.StripDecorators(title));
        Assert.True(FuzzyMatcher.CalculateTitleSimilarity(title, shorter) < 80);
    }

    [Theory]
    [InlineData("Song (with Guest)")]
    [InlineData("Song [with Guest]")]
    [InlineData("Song - with Guest")]
    [InlineData("Song with Guest")]
    [InlineData("Song feat. Guest")]
    [InlineData("Song featuring Guest")]
    [InlineData("Song ft. Guest")]
    public void TrailingCreditsCanBeNormalized(string title)
    {
        Assert.Equal("song", FuzzyMatcher.NormalizeTitleForMatching(title, "Artist, Guest"));
    }

    [Theory]
    [InlineData("U2", "ua")]
    [InlineData("24K Magic", "2ak magic")]
    [InlineData("1999", "iggg")]
    public void NormalizeForMatching_DoesNotFoldShortNamesOrNumericTokens(
        string source,
        string different)
    {
        Assert.NotEqual(
            FuzzyMatcher.NormalizeForMatching(source),
            FuzzyMatcher.NormalizeForMatching(different));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("Test", "", 0)]
    [InlineData("", "Test", 0)]
    public void CalculateSimilarity_EmptyStrings_ReturnsZero(string str1, string str2, int expected)
    {
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.Equal(expected, score);
    }

    [Fact]
    public void CalculateSimilarity_TokenOrder_DoesNotMatter()
    {
        var str1 = "Bright Side Mr";
        var str2 = "Mr Bright Side";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 90, $"Expected high score regardless of token order, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_PartialTokenMatch_ReturnsModerateScore()
    {
        var str1 = "Mr. Brightside";
        var str2 = "Mr. Brightside (Live)";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 70 && score < 100, $"Expected moderate score for partial match, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_SpecialCharacters_AreNormalized()
    {
        var str1 = "Café del Mar";
        var str2 = "Cafe del Mar";

        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        Assert.True(score >= 90, $"Expected high score for accented characters, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_StackallocAndHeapBranches_ReturnConsistentResults()
    {
        // stackalloc branch: targetLength + 1 <= 128
        Assert.Equal(42, FuzzyMatcher.CalculateSimilarity("kitten", "sitting"));

        // 127-char string -> 128 -> stackalloc (pure Levenshtein max is scaled to 75)
        var str127 = new string('a', 126) + "x";
        var str127b = new string('a', 126) + "y";
        Assert.True(FuzzyMatcher.CalculateSimilarity(str127, str127b) > 70);

        // 130-char string -> 131 -> heap fallback
        var str130 = new string('a', 129) + "x";
        var str130b = new string('a', 129) + "y";
        Assert.True(FuzzyMatcher.CalculateSimilarity(str130, str130b) > 70);
    }
}
