namespace allstarr.Tests;

public sealed class CacheCanonicalStateContractTests
{
    [Fact]
    public void ProductionCacheKeys_DoNotOwnCanonicalPlaylistState()
    {
        var root = FindRepositoryRoot();
        var source = Directory.GetFiles(Path.Combine(root, "allstarr"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(File.ReadAllText);
        var combined = string.Join('\n', source);

        Assert.DoesNotContain("spotify:matched:", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spotify:global-map:", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spotify:playlist:items:", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spotify:playlist:last-successful-sync:", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playlist:track-context:", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
