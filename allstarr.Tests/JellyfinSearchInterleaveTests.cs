using System.Reflection;
using System.Runtime.CompilerServices;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinSearchInterleaveTests
{
    [Fact]
    public void InterleaveByScore_PrimaryOnly_PreservesOriginalOrder()
    {
        var controller = CreateController();
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = InvokeInterleaveByScore(controller, primary, [], "bts", 5.0);

        Assert.Equal(["zzz filler", "BTS Anthem"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_SecondaryOnly_PreservesOriginalOrder()
    {
        var controller = CreateController();
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = InvokeInterleaveByScore(controller, [], secondary, "bts", 5.0);

        Assert.Equal(["zzz filler", "BTS Anthem"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_StrongerHeadMatch_LeadsWithoutReorderingSource()
    {
        var controller = CreateController();
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther remastered"),
            CreateItem("zzz filler")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther"),
            CreateItem("yyy filler")
        };

        var result = InvokeInterleaveByScore(controller, primary, secondary, "luther", 0.0);

        Assert.Equal(["luther", "luther remastered", "zzz filler", "yyy filler"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_TiedScores_PreferPrimaryQueueHead()
    {
        var controller = CreateController();
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("bts", "p1"),
            CreateItem("bts", "p2")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("bts", "s1"),
            CreateItem("bts", "s2")
        };

        var result = InvokeInterleaveByScore(controller, primary, secondary, "bts", 0.0);

        Assert.Equal(["p1", "p2", "s1", "s2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_StrongerLaterPrimaryHead_DoesNotBypassCurrentQueueHead()
    {
        var controller = CreateController();
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler", "p1"),
            CreateItem("bts local later", "p2")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("bts", "s1"),
            CreateItem("bts live", "s2")
        };

        var result = InvokeInterleaveByScore(controller, primary, secondary, "bts", 0.0);

        Assert.Equal(["s1", "s2", "p1", "p2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_JellyfinBoost_CanWinCloseHeadToHead()
    {
        var controller = CreateController();
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther remastered", "p1")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther", "s1")
        };

        var result = InvokeInterleaveByScore(controller, primary, secondary, "luther", 5.0);

        Assert.Equal(["p1", "s1"], result.Select(GetId));
    }

    [Fact]
    public void CalculateItemRelevanceScore_SongUsesArtistContext()
    {
        var controller = CreateController();
        var withArtist = CreateTypedItem("Audio", "cardigan", "song-with-artist");
        withArtist["Artists"] = new[] { "Taylor Swift" };

        var withoutArtist = CreateTypedItem("Audio", "cardigan", "song-without-artist");

        var withArtistScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", withArtist);
        var withoutArtistScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", withoutArtist);

        Assert.True(withArtistScore > withoutArtistScore);
    }

    [Fact]
    public void CalculateItemRelevanceScore_AlbumUsesArtistContext()
    {
        var controller = CreateController();
        var withArtist = CreateTypedItem("MusicAlbum", "folklore", "album-with-artist");
        withArtist["AlbumArtist"] = "Taylor Swift";

        var withoutArtist = CreateTypedItem("MusicAlbum", "folklore", "album-without-artist");

        var withArtistScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", withArtist);
        var withoutArtistScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", withoutArtist);

        Assert.True(withArtistScore > withoutArtistScore);
    }

    [Fact]
    public void CalculateItemRelevanceScore_ArtistIgnoresNonNameMetadata()
    {
        var controller = CreateController();
        var plainArtist = CreateTypedItem("MusicArtist", "Taylor Swift", "artist-plain");
        var noisyArtist = CreateTypedItem("MusicArtist", "Taylor Swift", "artist-noisy");
        noisyArtist["AlbumArtist"] = "Completely Different";
        noisyArtist["Artists"] = new[] { "Someone Else" };

        var plainScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", plainArtist);
        var noisyScore = InvokeCalculateItemRelevanceScore(controller, "taylor swift", noisyArtist);

        Assert.Equal(plainScore, noisyScore);
    }

    private static JellyfinController CreateController()
    {
        return (JellyfinController)RuntimeHelpers.GetUninitializedObject(typeof(JellyfinController));
    }

    private static List<Dictionary<string, object?>> InvokeInterleaveByScore(
        JellyfinController controller,
        List<Dictionary<string, object?>> primary,
        List<Dictionary<string, object?>> secondary,
        string query,
        double primaryBoost)
    {
        var method = typeof(JellyfinController).GetMethod(
            "InterleaveByScore",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (List<Dictionary<string, object?>>)method!.Invoke(
            controller,
            [primary, secondary, query, primaryBoost])!;
    }

    private static double InvokeCalculateItemRelevanceScore(
        JellyfinController controller,
        string query,
        Dictionary<string, object?> item)
    {
        var method = typeof(JellyfinController).GetMethod(
            "CalculateItemRelevanceScore",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(controller, [query, item])!;
    }

    private static Dictionary<string, object?> CreateItem(string name, string? id = null)
    {
        return new Dictionary<string, object?>
        {
            ["Name"] = name,
            ["Id"] = id ?? name
        };
    }

    private static Dictionary<string, object?> CreateTypedItem(string type, string name, string id)
    {
        var item = CreateItem(name, id);
        item["Type"] = type;
        return item;
    }

    private static string GetName(Dictionary<string, object?> item)
    {
        return item["Name"]?.ToString() ?? string.Empty;
    }

    private static string GetId(Dictionary<string, object?> item)
    {
        return item["Id"]?.ToString() ?? string.Empty;
    }
}
