using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class JellyfinLyricsWordGapTests
{
    [Theory]
    [InlineData("[00:01.00]<00:01.00>t'he 'a <00:02.00>last  ", "[00:01.00]<00:01.00>t'he 'a\u2800<00:02.00>last  ")]
    [InlineData("[00:01.000]first <00:02.125>second <00:03.5>last", "[00:01.000]first\u2800<00:02.125>second\u2800<00:03.5>last")]
    [InlineData("[00:01.00]<00:01.00>first\u2800<00:02.00>second <00:03.00>last", "[00:01.00]<00:01.00>first\u2800<00:02.00>second\u2800<00:03.00>last")]
    [InlineData("[ar:Artist]\r\n[00:01.00]line synced words  \r\n[00:02.00]next line", "[ar:Artist]\r\n[00:01.00]line synced words  \r\n[00:02.00]next line")]
    [InlineData("[00:01.00]<00:01.00>joined<00:02.00>word", "[00:01.00]<00:01.00>joined<00:02.00>word")]
    [InlineData("[00:01.00] <00:01.00>first \t<00:02.00>last  <00:03.00>", "[00:01.00] <00:01.00>first\u2800<00:02.00>last  <00:03.00>")]
    [InlineData("[ar:Artist <00:01.00>name]\nplain <00:01.00>words", "[ar:Artist <00:01.00>name]\nplain <00:01.00>words")]
    public void FileWordGapsProtectOnlySeparatorsAndAreIdempotent(string original, string expected)
    {
        var converted = JellyfinLyricsWordGap.Apply(original);
        Assert.Equal(expected, converted);
        Assert.Equal(expected, JellyfinLyricsWordGap.Apply(converted));
        Assert.False(char.IsWhiteSpace('\u2800'));
        Assert.Equal("word\u2800", "word\u2800".Trim());
    }
}
