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
    public void InterleaveByScore_LocalBoost_CanWinCloseHeadToHeadWithoutReorderingSource()
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

        var result = InvokeInterleaveByScore(controller, primary, secondary, "luther", 5.0);

        Assert.Equal(["luther remastered", "luther", "zzz filler", "yyy filler"], result.Select(GetName));
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

    private static Dictionary<string, object?> CreateItem(string name)
    {
        return new Dictionary<string, object?>
        {
            ["Name"] = name
        };
    }

    private static string GetName(Dictionary<string, object?> item)
    {
        return item["Name"]?.ToString() ?? string.Empty;
    }
}
