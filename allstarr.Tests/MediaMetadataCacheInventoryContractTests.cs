namespace allstarr.Tests;

public sealed class MediaMetadataCacheInventoryContractTests
{
    private readonly string _inventory = File.ReadAllText(
        FindRepositoryFile("docs", "architecture", "media-metadata-cache-inventory.md"));

    [Theory]
    [InlineData("Add playlist: source results")]
    [InlineData("Add playlist: source artwork")]
    [InlineData("Add playlist: target results and artwork")]
    [InlineData("Managed playlist list and detail")]
    [InlineData("Mapping review and shared track rows")]
    [InlineData("Event log details")]
    [InlineData("Playback/activity artwork")]
    [InlineData("Provider track/album/artist artwork")]
    [InlineData("Cached and kept media inventory")]
    public void Inventory_CoversEveryWebUiMediaSurface(string surface)
    {
        Assert.Contains(surface, _inventory, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_AssignsSafeIdentityLifecycleAndFallback()
    {
        Assert.Contains("Current owner / duplication", _inventory, StringComparison.Ordinal);
        Assert.Contains("Future stable identity and owner", _inventory, StringComparison.Ordinal);
        Assert.Contains("TTL and invalidation", _inventory, StringComparison.Ordinal);
        Assert.Contains("Negative cache and fallback", _inventory, StringComparison.Ordinal);
        Assert.Contains("tenant / authorization-scope / account / storefront / provider", _inventory, StringComparison.Ordinal);
        Assert.Contains("never contain credentials", _inventory, StringComparison.Ordinal);
        Assert.Contains("Stale-while-revalidate", _inventory, StringComparison.Ordinal);
        Assert.Contains("deterministic LRU/age cleanup", _inventory, StringComparison.Ordinal);
        Assert.Contains("may never", _inventory, StringComparison.Ordinal);
        Assert.Contains("canonical recordings", _inventory, StringComparison.Ordinal);
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
