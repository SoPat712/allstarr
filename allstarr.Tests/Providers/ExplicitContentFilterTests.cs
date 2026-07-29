using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Xunit;

namespace allstarr.Tests;

public class ExplicitContentFilterTests
{
    [Fact]
    public void ShouldIncludeSong_ShouldIncludeUnknownExplicitState()
    {
        var song = new Song { ExplicitContentLyrics = null };

        Assert.True(ExplicitContentFilter.ShouldIncludeSong(song, ExplicitFilter.All));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(song, ExplicitFilter.ExplicitOnly));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(song, ExplicitFilter.CleanOnly));
    }

    [Fact]
    public void ShouldIncludeSong_ExplicitOnly_ShouldExcludeOnlyCleanEditedValue3()
    {
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 0 }, ExplicitFilter.ExplicitOnly));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 1 }, ExplicitFilter.ExplicitOnly));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 2 }, ExplicitFilter.ExplicitOnly));
        Assert.False(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 3 }, ExplicitFilter.ExplicitOnly));
    }

    [Fact]
    public void ShouldIncludeSong_CleanOnly_ShouldExcludeExplicitValue1()
    {
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 0 }, ExplicitFilter.CleanOnly));
        Assert.False(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 1 }, ExplicitFilter.CleanOnly));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 3 }, ExplicitFilter.CleanOnly));
    }

    [Fact]
    public void ShouldIncludeSong_All_ShouldAlwaysInclude()
    {
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 1 }, ExplicitFilter.All));
        Assert.True(ExplicitContentFilter.ShouldIncludeSong(new Song { ExplicitContentLyrics = 3 }, ExplicitFilter.All));
    }
}
