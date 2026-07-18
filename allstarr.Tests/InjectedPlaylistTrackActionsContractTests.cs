namespace allstarr.Tests;

public sealed class InjectedPlaylistTrackActionsContractTests
{
    [Fact]
    public void InjectedTrackModal_ExposesAccessiblePerTrackMatchActions()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("aria-haspopup=\"menu\"", script, StringComparison.Ordinal);
        Assert.Contains("Search local library", script, StringComparison.Ordinal);
        Assert.Contains("Search music providers", script, StringComparison.Ordinal);
        Assert.Contains("Rematch automatically", script, StringComparison.Ordinal);
        Assert.Contains("Clear match", script, StringComparison.Ordinal);
        Assert.Contains(".track-action-popover", styles, StringComparison.Ordinal);
        Assert.Contains(".track-match-editor", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedTrackActions_UseTheLegacyPlaylistMappingBoundary()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("/api/admin/jellyfin/search?query=", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/external/search?", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/playlists/${encodeURIComponent(name)}/map", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/mappings/tracks?", script, StringComparison.Ordinal);
        Assert.Contains("await API.matchPlaylist(this.selectedInjectedPlaylist)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"squidwtf\">", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingLegacyMapping_RemovesTheRealKeysAndDerivedPlaylistCaches()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "MappingController.cs"));

        Assert.Contains("BuildSpotifyManualMappingKey(playlist, spotifyId)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyExternalMappingKey(playlist, spotifyId)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyMatchedTracksKey(playlist)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyLegacyMatchedTracksKey(playlist)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyPlaylistItemsKey(playlist)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyPlaylistStatsKey(playlist)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("manual:mapping:{playlist}:{spotifyId}", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPlaylistSummary_PopulatesTheExternalCountConsumedByTheWebUi()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "PlaylistController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.True(
            controller.Split("playlistInfo[\"externalTracks\"]", StringSplitOptions.None).Length >= 5,
            "Every playlist statistics path must populate the canonical externalTracks field.");
        Assert.Contains("BuildSpotifyPlaylistStatsKey(config.Name)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyMatchedTracksKey(config.Name)", controller, StringComparison.Ordinal);
        Assert.Contains("ExternalTrackPlaybackPolicy.CanUseForPlayback(", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyPlaylistStats(playlistInfo, canonicalLocal, canonicalExternal, canonicalMissing)", controller, StringComparison.Ordinal);
        Assert.Contains("ReadCachedString(item, \"ServerId\")", controller, StringComparison.Ordinal);
        Assert.Contains("playlistItemStatsApplied = true", controller, StringComparison.Ordinal);
        Assert.Contains("await _playlistFetcher.GetPlaylistTracksAsync(config.Name)", controller, StringComparison.Ordinal);
        Assert.Contains("var mapping = await _mappingService.GetMappingAsync(track.SpotifyId)", controller, StringComparison.Ordinal);
        Assert.Contains("matchedLocal + matchedExternal + matchedMissing == spotifyTrackCount", controller, StringComparison.Ordinal);
        Assert.Contains("display(playlist.externalTracks)", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
