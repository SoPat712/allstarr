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
    public void InterleaveByScore_TiedRounds_AlternatesSourcesInsteadOfDrainingPrimary()
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

        Assert.Equal(["p1", "s1", "p2", "s2"], result.Select(GetId));
    }

    [Fact]
    public void InterleaveByScore_StrongerLaterPrimaryHead_CanLeadSubsequentRoundWithoutReordering()
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

        Assert.Equal(["s1", "p1", "p2", "s2"], result.Select(GetId));
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

    private static Dictionary<string, object?> CreateItem(string name, string? id = null)
    {
        return new Dictionary<string, object?>
        {
            ["Name"] = name,
            ["Id"] = id ?? name
        };
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
