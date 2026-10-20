using allstarr.Controllers;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class JellyfinSearchInterleaveTests
{
    [Fact]
    public void InterleaveByScore_PrimaryOnly_RanksByRelevance()
    {
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = Rank(primary, [], "bts");

        Assert.Equal(["BTS Anthem", "zzz filler"], result.Select(GetName));
    }

    [Fact]
    public void InterleaveByScore_SecondaryOnly_RanksByRelevance()
    {
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("zzz filler"),
            CreateItem("BTS Anthem")
        };

        var result = Rank([], secondary, "bts");

        Assert.Equal(["BTS Anthem", "zzz filler"], result.Select(GetName));
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

        var result = Rank(primary, secondary, "luther");

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

        var result = Rank(primary, secondary, "bts");

        Assert.Equal(["p1", "p2", "s1", "s2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_StrongerLaterMatch_BypassesWeakEarlierResult()
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

        var result = Rank(primary, secondary, "bts");

        Assert.Equal(["s1", "p2", "s2", "p1"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_StrongerProviderMatch_WinsWithoutLocalBoost()
    {
        var primary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther remastered", "p1")
        };
        var secondary = new List<Dictionary<string, object?>>
        {
            CreateItem("luther", "s1")
        };

        var result = Rank(primary, secondary, "luther");

        Assert.Equal(["s1", "p1"], result.Select(GetId));
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
    public void MixAlbumMetadataKeepsTheOriginalFirstWhenSearchResultsAreMerged()
    {
        var original = CreateTypedItem("Audio", "Espresso", "original");
        var mix = CreateTypedItem("Audio", "Espresso", "mix");
        mix["Album"] = "Barbie: Live from the Barbie Dream House (DJ Mix)";

        var result = Rank([], [mix, original], "Espresso");

        Assert.Equal(["original", "mix"], result.Select(GetId));
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

    private static List<Dictionary<string, object?>> Rank(
        List<Dictionary<string, object?>> local, List<Dictionary<string, object?>> external, string query) =>
        SearchRelevanceRanker.Merge(local, external, item => JellyfinController.CalculateItemRelevanceScore(query, item));

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
