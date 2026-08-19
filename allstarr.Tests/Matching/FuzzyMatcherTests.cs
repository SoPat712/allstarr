using Xunit;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class FuzzyMatcherTests
{
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
        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= expectedMin, $"Expected score >= {expectedMin}, got {score}");
    }

    [Theory]
    [InlineData("Mr. Brightside", "Somebody Told Me", 20)]
    [InlineData("The Killers", "The Beatles", 40)]
    [InlineData("Hot Fuss", "Sam's Town", 20)]
    public void CalculateSimilarity_DifferentStrings_ReturnsLowScore(string str1, string str2, int expectedMax)
    {
        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score <= expectedMax, $"Expected score <= {expectedMax}, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IgnoresPunctuation()
    {
        // Arrange
        var str1 = "Don't Stop Believin'";
        var str2 = "Dont Stop Believin";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= 95, $"Expected high score for punctuation differences, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IgnoresCase()
    {
        // Arrange
        var str1 = "Mr. Brightside";
        var str2 = "mr. brightside";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.Equal(100, score);
    }

    [Fact]
    public void CalculateSimilarity_HandlesArticles()
    {
        // Arrange
        var str1 = "The Killers";
        var str2 = "Killers";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= 80, $"Expected high score when 'The' is removed, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_HandlesFeaturedArtists()
    {
        // Arrange
        var str1 = "Song Title (feat. Artist)";
        var str2 = "Song Title";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= 70, $"Expected decent score for featured artist variations, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_HandlesRemixes()
    {
        // Arrange
        var str1 = "Song Title - Radio Edit";
        var str2 = "Song Title";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
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
        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.Equal(expected, score);
    }

    [Fact]
    public void CalculateSimilarity_TokenOrder_DoesNotMatter()
    {
        // Arrange
        var str1 = "Bright Side Mr";
        var str2 = "Mr Bright Side";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= 90, $"Expected high score regardless of token order, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_PartialTokenMatch_ReturnsModerateScore()
    {
        // Arrange
        var str1 = "Mr. Brightside";
        var str2 = "Mr. Brightside (Live)";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
        Assert.True(score >= 70 && score < 100, $"Expected moderate score for partial match, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_SpecialCharacters_AreNormalized()
    {
        // Arrange
        var str1 = "Café del Mar";
        var str2 = "Cafe del Mar";

        // Act
        var score = FuzzyMatcher.CalculateSimilarity(str1, str2);

        // Assert
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
