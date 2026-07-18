namespace allstarr.Tests;

public sealed class PlaylistItemsRecoveryContractTests
{
    [Fact]
    public void RetainedMatches_RebuildMissingPlayerItemsBeforeSkipping()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Spotify", "SpotifyTrackMatchingService.cs"));

        Assert.Contains("EnsurePlaylistItemsCacheAsync", source, StringComparison.Ordinal);
        Assert.Contains("Rebuilding missing player playlist cache", source, StringComparison.Ordinal);
        Assert.Contains("existingMatched ?? []", source, StringComparison.Ordinal);
        Assert.Contains("existingItems is { Count: > 0 }", source, StringComparison.Ordinal);
        Assert.Contains("EnsureLegacyPlaylistItemsCacheAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyMatchedTracksKey(playlistName)", source, StringComparison.Ordinal);
        Assert.Contains("source.Tracks", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
