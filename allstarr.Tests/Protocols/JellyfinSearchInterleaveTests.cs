using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinSearchInterleaveTests
{
    [Fact]
    public void InterleaveByScore_PrimaryOnly_PreservesOriginalOrder()
    {
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = JellyfinController.InterleaveByScore(primary, [], "bts", 5.0);

        Assert.Equal(["zzz filler", "BTS Anthem"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_SecondaryOnly_PreservesOriginalOrder()
    {
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = JellyfinController.InterleaveByScore([], secondary, "bts", 5.0);

        Assert.Equal(["zzz filler", "BTS Anthem"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_StrongerHeadMatch_LeadsWithoutReorderingSource()
    {
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

        var result = JellyfinController.InterleaveByScore(primary, secondary, "luther", 0.0);

        Assert.Equal(["luther", "luther remastered", "zzz filler", "yyy filler"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_TiedScores_PreferPrimaryQueueHead()
    {
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

        var result = JellyfinController.InterleaveByScore(primary, secondary, "bts", 0.0);

        Assert.Equal(["p1", "p2", "s1", "s2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_StrongerLaterPrimaryHead_DoesNotBypassCurrentQueueHead()
    {
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

        var result = JellyfinController.InterleaveByScore(primary, secondary, "bts", 0.0);

        Assert.Equal(["s1", "s2", "p1", "p2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_JellyfinBoost_CanWinCloseHeadToHead()
    {
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther remastered", "p1")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther", "s1")
        };

        var result = JellyfinController.InterleaveByScore(primary, secondary, "luther", 5.0);

        Assert.Equal(["p1", "s1"], result.Select(GetId));
    }

    [Fact]
    public void CalculateItemRelevanceScore_SongUsesArtistContext()
    {
        var withArtist = CreateTypedItem("Audio", "cardigan", "song-with-artist");
        withArtist["Artists"] = new[] { "Taylor Swift" };

        var withoutArtist = CreateTypedItem("Audio", "cardigan", "song-without-artist");

        var withArtistScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", withArtist);
        var withoutArtistScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", withoutArtist);

        Assert.True(withArtistScore > withoutArtistScore);
    }

    [Fact]
    public void CalculateItemRelevanceScore_AlbumUsesArtistContext()
    {
        var withArtist = CreateTypedItem("MusicAlbum", "folklore", "album-with-artist");
        withArtist["AlbumArtist"] = "Taylor Swift";

        var withoutArtist = CreateTypedItem("MusicAlbum", "folklore", "album-without-artist");

        var withArtistScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", withArtist);
        var withoutArtistScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", withoutArtist);

        Assert.True(withArtistScore > withoutArtistScore);
    }

    [Fact]
    public void CalculateItemRelevanceScore_ArtistIgnoresNonNameMetadata()
    {
        var plainArtist = CreateTypedItem("MusicArtist", "Taylor Swift", "artist-plain");
        var noisyArtist = CreateTypedItem("MusicArtist", "Taylor Swift", "artist-noisy");
        noisyArtist["AlbumArtist"] = "Completely Different";
        noisyArtist["Artists"] = new[] { "Someone Else" };

        var plainScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", plainArtist);
        var noisyScore = JellyfinController.CalculateItemRelevanceScore("taylor swift", noisyArtist);

        Assert.Equal(plainScore, noisyScore);
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
