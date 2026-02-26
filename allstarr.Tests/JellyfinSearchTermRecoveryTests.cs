using System.Reflection;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinSearchTermRecoveryTests
{
    [Fact]
    public void RecoverSearchTermFromRawQuery_PreservesUnencodedAmpersand()
    {
        var raw = "?SearchTerm=Love%20&%20Hyperbole&Recursive=true&IncludeItemTypes=MusicAlbum";
        var recovered = InvokePrivateStatic<string?>("RecoverSearchTermFromRawQuery", raw);

        Assert.Equal("Love & Hyperbole", recovered);
    }

    [Fact]
    public void GetEffectiveSearchTerm_PrefersRecoveredWhenBoundIsTruncated()
    {
        var bound = "Love ";
        var raw = "?SearchTerm=Love%20&%20Hyperbole&Recursive=true";
        var effective = InvokePrivateStatic<string?>("GetEffectiveSearchTerm", bound, raw);

        Assert.Equal("Love & Hyperbole", effective);
    }

    [Fact]
    public void GetEffectiveSearchTerm_UsesBoundWhenRecoveredIsMissing()
    {
        var bound = "Love & Hyperbole";
        var raw = "?Recursive=true&IncludeItemTypes=MusicAlbum";
        var effective = InvokePrivateStatic<string?>("GetEffectiveSearchTerm", bound, raw);

        Assert.Equal("Love & Hyperbole", effective);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(JellyfinController).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, args);
        return (T)result!;
    }
}
