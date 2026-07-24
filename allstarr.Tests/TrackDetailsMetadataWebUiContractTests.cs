namespace allstarr.Tests;

public sealed class TrackDetailsMetadataWebUiContractTests
{
    private readonly string script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void TrackDetails_ExposeIdentifiersAndRouteProvenanceOnDemand()
    {
        Assert.Contains("Identifiers and route", script, StringComparison.Ordinal);
        Assert.Contains("metadata.isrc || primaryLocal.isrc", script, StringComparison.Ordinal);
        Assert.Contains("Source track ID", script, StringComparison.Ordinal);
        Assert.Contains("Target item ID", script, StringComparison.Ordinal);
        Assert.Contains("Route provenance", script, StringComparison.Ordinal);
        Assert.Contains("identity.externalId", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackDetails_ShowAvailableMediaFactsWithoutClutteringRows()
    {
        Assert.Contains("Media facts", script, StringComparison.Ordinal);
        Assert.Contains("[\"Codec\", media.codec || context.codec || context.audioCodec]", script, StringComparison.Ordinal);
        Assert.Contains("[\"Bitrate\", media.bitrate || context.bitrate", script, StringComparison.Ordinal);
        Assert.Contains("[\"Bit depth\", media.bitDepth || context.bitDepth", script, StringComparison.Ordinal);
        Assert.Contains("[\"Sample rate\", media.sampleRate || context.sampleRate", script, StringComparison.Ordinal);
        Assert.Contains("artifacts.map((artifact)", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
