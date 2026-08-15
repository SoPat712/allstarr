using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinSearchTermRecoveryTests
{
    [Fact]
    public void RecoverSearchTermFromRawQuery_PreservesUnencodedAmpersand()
    {
        var raw = "?SearchTerm=Love%20&%20Hyperbole&Recursive=true&IncludeItemTypes=MusicAlbum";
        var recovered = JellyfinController.RecoverSearchTermFromRawQuery(raw);

        Assert.Equal("Love & Hyperbole", recovered);
    }

    [Fact]
    public void GetEffectiveSearchTerm_PrefersRecoveredWhenBoundIsTruncated()
    {
        var bound = "Love ";
        var raw = "?SearchTerm=Love%20&%20Hyperbole&Recursive=true";
        var effective = JellyfinController.GetEffectiveSearchTerm(bound, raw);

        Assert.Equal("Love & Hyperbole", effective);
    }

    [Fact]
    public void GetEffectiveSearchTerm_UsesBoundWhenRecoveredIsMissing()
    {
        var bound = "Love & Hyperbole";
        var raw = "?Recursive=true&IncludeItemTypes=MusicAlbum";
        var effective = JellyfinController.GetEffectiveSearchTerm(bound, raw);

        Assert.Equal("Love & Hyperbole", effective);
    }

}
