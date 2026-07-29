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

    [Fact]
    public void UserSearchHints_AreTranslatedToJellyfinTwelveQueryShape()
    {
        var source = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "JellyfinController.Search.cs"));

        Assert.Contains("const string endpoint = \"Search/Hints\"", source, StringComparison.Ordinal);
        Assert.Contains("queryParams[\"UserId\"] = userId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"Users/{userId}/Search/Hints\"", source, StringComparison.Ordinal);
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

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
