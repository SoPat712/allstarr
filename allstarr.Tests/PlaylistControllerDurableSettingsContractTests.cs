namespace allstarr.Tests;

public sealed class PlaylistControllerDurableSettingsContractTests
{
    private static readonly string Source = File.ReadAllText(FindRepositoryFile(
        "allstarr", "Controllers", "PlaylistController.cs"));

    [Fact]
    public void InjectedPlaylists_ReadAndWriteTenantDurableSettings()
    {
        Assert.Contains("GetConfiguredPlaylistsAsync()", Source, StringComparison.Ordinal);
        Assert.Contains("settings.GetAsync(tenantId, \"SpotifyImport:Playlists\"", Source, StringComparison.Ordinal);
        Assert.Contains("settings.ApplyBatchAsync(", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadPlaylistsFromEnvFileAsync()", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateEnvConfigAsync(", Source, StringComparison.Ordinal);
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

        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)} from the test directory.");
    }
}
