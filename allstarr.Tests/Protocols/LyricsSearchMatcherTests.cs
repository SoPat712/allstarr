using allstarr.Core.ClientApi;

namespace allstarr.Tests;

public sealed class LyricsSearchMatcherTests
{
    [Theory]
    [InlineData("blue moon", null, "blue moon", 1)]
    [InlineData("blue", "moon rising", "blue moon", 1)]
    [InlineData("blue moon", null, "moon blue", 0.8)]
    [InlineData("blue moonlight", null, "blue moon", 0.7)]
    [InlineData("a blue sky", null, "blue moon", 0.45)]
    [InlineData("a blue sky", null, "blue blue moon", 0.45)]
    [InlineData("red sun", "blue moon", "blue moon", 0)]
    [InlineData("blue", "moonlight blue moon", "blue moon", 0.45)]
    [InlineData("blueberry moon", null, "blue moon", 0.7)]
    [InlineData("the", null, "he", 0)]
    [InlineData("", "blue moon", "blue moon", 0)]
    [InlineData("blue moon", null, "", 0)]
    public void LyricsSearch_ScoringPreservesBoundariesAndDistinctQueryWords(string line, string? next, string query, double expected)
    {
        Assert.Equal(expected, LyricsSearchMatcher.Score(line, next, query), precision: 10);
    }

    [Fact]
    public void LyricsSearch_PhrasePreservesUtf16OffsetsAndCombiningAccents()
    {
        var match = LyricsSearchMatcher.Match("😀 Café e\u0301toile!", null, "cafe etoile");
        Assert.Equal(1, match.Score);
        var highlight = Assert.Single(match.Highlights);
        Assert.Equal(3, highlight.Start);
        Assert.Equal("Café e\u0301toile", "😀 Café e\u0301toile!".Substring(highlight.Start, highlight.Length));
    }

    [Fact]
    public void LyricsSearch_ExactHighlightUsesTheWholeTokenOccurrence()
    {
        var match = LyricsSearchMatcher.Match("the he", null, "he");
        Assert.Equal(1, match.Score);
        Assert.Equal(new LyricsSearchHighlight(4, 2), Assert.Single(match.Highlights));
    }

    [Fact]
    public void LyricsSearch_UnorderedWholeWordsHighlightTheirOwnTokens()
    {
        var match = LyricsSearchMatcher.Match("the he", null, "he the");
        Assert.Equal(0.8, match.Score);
        Assert.Equal(new[] { new LyricsSearchHighlight(0, 3), new LyricsSearchHighlight(4, 2) }, match.Highlights);
    }

    [Theory]
    [InlineData("ΟΣ", "οσ")]
    [InlineData("I’ve,   heard there's more", "ive heard theres")]
    [InlineData("I'VE—heard\tthere’s more", "I’ve heard there's")]
    public void LyricsSearch_IgnoresPunctuationApostrophesAndSpacing(string line, string query)
    {
        Assert.Equal(1, LyricsSearchMatcher.Match(line, null, query).Score);
    }

    [Fact]
    public void LyricsSearch_CrossLineReportsOnlyFirstLineHighlights()
    {
        var match = LyricsSearchMatcher.Match("Take my hand", "and follow me", "hand and follow");
        Assert.Equal(1, match.Score);
        Assert.Equal(new LyricsSearchHighlight(8, 4), Assert.Single(match.Highlights));
        Assert.Empty(LyricsSearchMatcher.Match("Take my hand", "and follow me", "follow me").Highlights);
    }

    [Fact]
    public void LyricsSearch_RanksPhraseAboveWordsAbovePartial()
    {
        var phrase = LyricsSearchMatcher.Match("blue moon", null, "blue moon");
        var words = LyricsSearchMatcher.Match("moon in a blue sky", null, "blue moon");
        var partial = LyricsSearchMatcher.Match("a blue sky", null, "blue moon");
        Assert.True(phrase.Score > words.Score);
        Assert.True(words.Score > partial.Score);
        Assert.True(partial.Score > 0);
        Assert.Equal(0, LyricsSearchMatcher.Match("red sun", null, "blue moon").Score);
    }
}
