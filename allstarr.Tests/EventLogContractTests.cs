namespace allstarr.Tests;

public sealed class EventLogContractTests
{
    [Fact]
    public void ActivityProjection_DescribesDurableTrackMatches()
    {
        var controller = Read("allstarr/Controllers/AdminUiController.cs");

        Assert.Contains("MatchActivityLabel(item.State)", controller, StringComparison.Ordinal);
        Assert.Contains("MatchActivityDetail(item, snapshot, identity", controller, StringComparison.Ordinal);
        Assert.Contains("ExternalMetadataSnapshots.AsNoTracking()", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderTrackIdentities.AsNoTracking()", controller, StringComparison.Ordinal);
        Assert.Contains("LibraryTracks.AsNoTracking()", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void EventLog_GroupsAndExpandsConsecutiveEvents()
    {
        var script = Read("allstarr/wwwroot/js/webui.js");
        var styles = Read("allstarr/wwwroot/css/workspaces.css");

        Assert.Contains("previous?.key === key", script, StringComparison.Ordinal);
        Assert.Contains("<details class=\"event-log-group\"", script, StringComparison.Ordinal);
        Assert.Contains("scrobble: \"headphones\"", script, StringComparison.Ordinal);
        Assert.Contains("event-log-collapse", script, StringComparison.Ordinal);
        Assert.Contains(".event-log-group[open]", styles, StringComparison.Ordinal);
        Assert.Contains(".event-log-detail", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedIcons_IncludeScrobblingHeadphones()
    {
        Assert.Contains("\"headphones\"", Read("allstarr/wwwroot/js/ui/icons.js"), StringComparison.Ordinal);
        Assert.Contains("id=\"headphones\"", Read("allstarr/wwwroot/images/ui-icons.svg"), StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
