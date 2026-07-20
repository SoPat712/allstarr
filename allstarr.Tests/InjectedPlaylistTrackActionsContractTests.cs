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

    [Fact]
    public void PlaylistDetails_UseMaterializedOrderAndReturnAuthoritativeCounts()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "PlaylistController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("MatchMaterializedItems(spotifyTracks, materializedItems)", controller, StringComparison.Ordinal);
        Assert.Contains("MaterializedIdentityMatches", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("cachedItem = cachedPlaylistItems[trackIndex]", controller, StringComparison.Ordinal);
        Assert.Contains("totalPlayable = matchedTrackCount", controller, StringComparison.Ordinal);
        Assert.Contains("localTracks = localTrackCount", controller, StringComparison.Ordinal);
        Assert.Contains("externalTracks = externalTrackCount", controller, StringComparison.Ordinal);
        Assert.Contains("matchState = isLocal == true ? \"local\"", controller, StringComparison.Ordinal);
        Assert.Contains("backendItemId = isLocal == true", controller, StringComparison.Ordinal);
        Assert.Contains("FuzzyMatcher.StripDecorators", File.ReadAllText(FindRepositoryFile("allstarr", "Services", "Admin", "PlaylistTrackStatusResolver.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("playlistSummary?.totalPlayable", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistDetails_ExposeSyncTimingBreakdownAndDirectRematch()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "PlaylistController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("lastSourceRefreshAt", controller, StringComparison.Ordinal);
        Assert.Contains("playlistMetadata?.FetchedAt", controller, StringComparison.Ordinal);
        Assert.Contains("nextSyncAt", controller, StringComparison.Ordinal);
        Assert.Contains("matchStatus", controller, StringComparison.Ordinal);
        Assert.Contains("Local</small>", script, StringComparison.Ordinal);
        Assert.Contains("External</small>", script, StringComparison.Ordinal);
        Assert.Contains("Unmatched</small>", script, StringComparison.Ordinal);
        Assert.Contains("Next rematch", script, StringComparison.Ordinal);
        Assert.Contains("Sync & rematch", script, StringComparison.Ordinal);
        Assert.Contains("playlist-rematch-action", script, StringComparison.Ordinal);
        var summaryStart = script.IndexOf("playlist-operation-summary\" aria-label=\"Playlist synchronization details", StringComparison.Ordinal);
        var summaryEnd = script.IndexOf("</div>", summaryStart, StringComparison.Ordinal);
        var action = script.IndexOf("playlist-rematch-action", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart && action > summaryEnd,
            "The rematch action must be a sibling of the synchronization stat strip, not nested inside it.");
        Assert.Contains("Current source snapshot needs matching", script, StringComparison.Ordinal);
        Assert.Contains(".playlist-operation-summary", styles, StringComparison.Ordinal);
        Assert.Contains(".playlist-rematch-action", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTrackRows_OpenAnAuthoritativeMappingHistoryDialog()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "TrackMatchesController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("[HttpGet(\"spotify/{spotifyId}\")]", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderTrackIdentities", controller, StringComparison.Ordinal);
        Assert.Contains("ExternalMetadataSnapshots", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderDownloadArtifacts", controller, StringComparison.Ordinal);
        Assert.Contains("spotifyMappings.GetMappingAsync(spotifyId)", controller, StringComparison.Ordinal);
        Assert.Contains("policyVersion = \"compatibility-v2\"", controller, StringComparison.Ordinal);
        Assert.Contains("source = \"materialized Jellyfin playlist\"", controller, StringComparison.Ordinal);
        Assert.Contains("trackMappingDetails:", script, StringComparison.Ordinal);
        Assert.Contains("backendItemId", script, StringComparison.Ordinal);
        Assert.Contains("Open mapping details for", script, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"track-details-dialog\"", script, StringComparison.Ordinal);
        Assert.Contains("Identifiers and destinations", script, StringComparison.Ordinal);
        Assert.Contains("Match decisions", script, StringComparison.Ordinal);
        Assert.Contains("Recorded routing decisions", script, StringComparison.Ordinal);
        Assert.Contains("Cache and downloads", script, StringComparison.Ordinal);
        Assert.Contains("Track activity", script, StringComparison.Ordinal);
        Assert.Contains("track-details-dialog redesigned-dialog", script, StringComparison.Ordinal);
        Assert.Contains("track-details-scroll", script, StringComparison.Ordinal);
        Assert.Contains(".track-details-dialog", styles, StringComparison.Ordinal);
        Assert.Contains("z-index: 1020", styles, StringComparison.Ordinal);
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
