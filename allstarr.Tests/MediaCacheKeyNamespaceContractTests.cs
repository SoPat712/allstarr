namespace allstarr.Tests;

public sealed class MediaCacheKeyNamespaceContractTests
{
    private static readonly string[] ProductionConsumers =
    [
        Path.Combine("allstarr", "Controllers", "JellyfinController.PlaylistHandler.cs"),
        Path.Combine("allstarr", "Controllers", "SubSonicController.cs"),
        Path.Combine("allstarr", "Services", "Jellyfin", "JellyfinProxyService.cs"),
        Path.Combine("allstarr", "Services", "Spotify", "SpotifyPlaylistFetcher.cs"),
        Path.Combine("allstarr", "Services", "Spotify", "SpotifyPlaylistMatchingAdapter.cs")
    ];

    [Fact]
    public void MediaCacheConsumers_UseCentralKeyBuilder()
    {
        var sources = ProductionConsumers
            .Select(path => File.ReadAllText(FindRepositoryFile(path)))
            .ToArray();

        Assert.DoesNotContain(sources, source => source.Contains("$\"playlist:image:", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("$\"image:", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains(
            "CacheKeyBuilder.BuildPlaylistImageKey",
            StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains(
            "CacheKeyBuilder.BuildJellyfinImageKey",
            StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains(
            "CacheKeyBuilder.BuildJellyfinImagePattern",
            StringComparison.Ordinal));
    }

    [Fact]
    public void MediaCacheKeyDocumentation_IsBackendNeutral()
    {
        var builder = File.ReadAllText(
            FindRepositoryFile(Path.Combine("allstarr", "Services", "Common", "CacheKeyBuilder.cs")));

        Assert.Contains("bounded disk-backed media tier", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Images are cached as byte[] in Redis", builder, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
