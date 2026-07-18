namespace allstarr.Tests;

public sealed class LegacyMappingReadinessContractTests
{
    [Fact]
    public void ImportedMappings_ReportPlayableAndReviewCounts()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "MappingController.cs"));

        Assert.Contains("ExternalTrackPlaybackPolicy.CanUseForPlayback", controller, StringComparison.Ordinal);
        Assert.Contains("playableCount", controller, StringComparison.Ordinal);
        Assert.Contains("needsReviewCount", controller, StringComparison.Ordinal);
        Assert.Contains("status = playable ? \"ready\" : \"needs_review\"", controller, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
