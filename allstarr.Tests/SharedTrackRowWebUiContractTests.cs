namespace allstarr.Tests;

public sealed class SharedTrackRowWebUiContractTests
{
    private readonly string _script = Read("allstarr/wwwroot/js/webui.js");
    private readonly string _styles = Read("allstarr/wwwroot/css/base.css");

    [Fact]
    public void TrackSurfaces_UseOneNormalizedIdentityRenderer()
    {
        Assert.Contains("renderSharedTrackRow(track, options = {})", _script, StringComparison.Ordinal);
        Assert.Contains("this.renderSharedTrackRow(track, {", _script, StringComparison.Ordinal);
        Assert.Contains("this.renderSharedTrackRow(mapping, {", _script, StringComparison.Ordinal);
        Assert.Contains("this.renderSharedTrackRow(file, {", _script, StringComparison.Ordinal);
        Assert.Contains("this.renderSharedTrackRow(item, {", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedRenderer_OwnsArtworkMetadataProviderDurationAndActionSlots()
    {
        Assert.Contains("shared-track-row", _script, StringComparison.Ordinal);
        Assert.Contains("track-primary-action", _script, StringComparison.Ordinal);
        Assert.Contains("track-provider-cell", _script, StringComparison.Ordinal);
        Assert.Contains("track-duration-cell", _script, StringComparison.Ordinal);
        Assert.Contains("options.actions", _script, StringComparison.Ordinal);
        Assert.Contains(".shared-track-row {", _styles, StringComparison.Ordinal);
        Assert.Contains(".shared-track-row-grid", _styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 620px)", _styles, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            relativePath));
        return File.ReadAllText(path);
    }
}
