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

    [Fact]
    public void InjectedPlaylistReadModels_ExposeArtworkMatchingAndScheduleState()
    {
        Assert.Contains("[\"artworkUrl\"]", Source, StringComparison.Ordinal);
        Assert.Contains("playlistMetadata?.ImageUrl", Source, StringComparison.Ordinal);
        Assert.Contains("playlistInfo[\"artworkSource\"] = !string.IsNullOrWhiteSpace", Source, StringComparison.Ordinal);
        Assert.Contains("? \"playlist\"", Source, StringComparison.Ordinal);
        Assert.Contains("? \"track_fallback\"", Source, StringComparison.Ordinal);
        Assert.Contains("[\"matchedTracks\"]", Source, StringComparison.Ordinal);
        Assert.Contains("[\"unmatchedTracks\"]", Source, StringComparison.Ordinal);
        Assert.Contains("[\"matchPercent\"]", Source, StringComparison.Ordinal);
        Assert.Contains("[\"syncStatus\"]", Source, StringComparison.Ordinal);
        Assert.Contains("[\"providerBreakdown\"]", Source, StringComparison.Ordinal);
        Assert.Contains("[\"nextSyncAt\"]", Source, StringComparison.Ordinal);
        Assert.Contains("MatchMaterializedItems(sourceTracks, materializedItems)", Source, StringComparison.Ordinal);
        Assert.Contains("currentSummaryShape", Source, StringComparison.Ordinal);
        Assert.Contains("PlaylistSummarySchemaVersion", Source, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = PlaylistSummarySchemaVersion", Source, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistInventoryAsync(configuredPlaylists)", Source, StringComparison.Ordinal);
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
