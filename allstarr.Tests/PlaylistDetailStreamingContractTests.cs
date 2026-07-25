namespace allstarr.Tests;

public sealed class PlaylistDetailStreamingContractTests
{
    private readonly string _script = Read("allstarr/wwwroot/js/webui.js");
    private readonly string _baseStyles = Read("allstarr/wwwroot/css/base.css");
    private readonly string _workspaceStyles = Read("allstarr/wwwroot/css/workspaces.css");
    private readonly string _responsiveStyles = Read("allstarr/wwwroot/css/responsive.css");
    private readonly string _iconRegistry = Read("allstarr/wwwroot/js/ui/icons.js");
    private readonly string _iconSprite = Read("allstarr/wwwroot/images/ui-icons.svg");

    [Fact]
    public void SynchronizationSummary_GroupsCoverageAndTiming()
    {
        Assert.Contains("playlist-operation-group coverage-group", _script, StringComparison.Ordinal);
        Assert.Contains("playlist-operation-group timing-group", _script, StringComparison.Ordinal);
        Assert.Contains("playlist-operation-metrics", _script, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, .84fr) minmax(0, 1.16fr)", _workspaceStyles, StringComparison.Ordinal);
        Assert.Contains(".playlist-operation-group {\n    display: grid;\n    grid-template-columns: minmax(0, 1fr)", _workspaceStyles, StringComparison.Ordinal);
        Assert.Contains("grid-column: 1 / -1", _workspaceStyles, StringComparison.Ordinal);
        Assert.Contains(".playlist-operation-heading", _workspaceStyles, StringComparison.Ordinal);
        var tabletBreakpoint = _responsiveStyles.IndexOf("@media (max-width: 900px)", StringComparison.Ordinal);
        var tabletGroup = _responsiveStyles.IndexOf(".playlist-operation-group", tabletBreakpoint, StringComparison.Ordinal);
        var tabletGroupEnd = _responsiveStyles.IndexOf('}', tabletGroup);
        Assert.True(tabletBreakpoint >= 0 && tabletGroup > tabletBreakpoint && tabletGroupEnd > tabletGroup);
        Assert.Contains(
            "grid-template-columns: minmax(140px, .72fr) minmax(0, 1.28fr)",
            _responsiveStyles[tabletGroup..tabletGroupEnd],
            StringComparison.Ordinal);
        var compactBreakpoint = _responsiveStyles.IndexOf("@media (max-width: 620px)", StringComparison.Ordinal);
        var compactSummary = _responsiveStyles.IndexOf(".playlist-operation-summary", compactBreakpoint, StringComparison.Ordinal);
        var compactSummaryEnd = _responsiveStyles.IndexOf('}', compactSummary);
        Assert.True(compactBreakpoint >= 0 && compactSummary > compactBreakpoint && compactSummaryEnd > compactSummary);
        Assert.Contains(
            "grid-template-columns: minmax(0, 1fr)",
            _responsiveStyles[compactSummary..compactSummaryEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrackRows_UseStreamingHierarchyAndCanonicalMetadata()
    {
        Assert.Contains("renderPlaylistTrackRow(track, index, targetBackend)", _script, StringComparison.Ordinal);
        Assert.Contains("track-primary-action", _script, StringComparison.Ordinal);
        Assert.Contains("track-byline", _script, StringComparison.Ordinal);
        Assert.Contains("track-album", _script, StringComparison.Ordinal);
        Assert.Contains("track-duration-cell", _script, StringComparison.Ordinal);
        Assert.Contains("track-lyrics-indicator", _script, StringComparison.Ordinal);
        Assert.Contains("track.isrc ? `ISRC ${track.isrc}`", _script, StringComparison.Ordinal);
        Assert.Contains("track.spotifyId ? `Spotify ${track.spotifyId}`", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("track-artist-cell\" data-label=\"Artist\"", _script, StringComparison.Ordinal);
        Assert.Contains(".track-primary-action", _baseStyles, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-row .track-duration-cell", _responsiveStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackActions_UseCenteredVerticalEllipsisWithoutNestedRowButton()
    {
        Assert.Contains("icon(\"moreVertical\", 18)", _script, StringComparison.Ordinal);
        Assert.Contains("\"moreVertical\"", _iconRegistry, StringComparison.Ordinal);
        Assert.Contains("id=\"moreVertical\"", _iconSprite, StringComparison.Ordinal);
        Assert.Contains("width: 40px", _baseStyles, StringComparison.Ordinal);
        Assert.Contains("place-items: center", _baseStyles, StringComparison.Ordinal);
        Assert.Contains("<article class=\"playlist-track-row playlist-track-inspectable\"", _script, StringComparison.Ordinal);
        Assert.Contains("playlist-track-inspectable\" tabindex=\"0\" role=\"button\"", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-track-inspectable\" role=\"button\"", _script, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
