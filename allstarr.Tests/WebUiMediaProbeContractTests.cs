namespace allstarr.Tests;

public sealed class WebUiMediaProbeContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void SettingsExposeReadOnlyMetadataAndArtworkProbe()
    {
        Assert.Contains("/api/admin/media-probe", _script, StringComparison.Ordinal);
        Assert.Contains("Test metadata and artwork", _script, StringComparison.Ordinal);
        Assert.Contains("this.runMediaProbe", _script, StringComparison.Ordinal);
        Assert.Contains("Media pipeline ready", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaProbeExplainsItsPrivacyBoundary()
    {
        Assert.Contains(
            "It does not reveal track names, IDs, credentials, or server addresses.",
            _script,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)}");
    }
}
