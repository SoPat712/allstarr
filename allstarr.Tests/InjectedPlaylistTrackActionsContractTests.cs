namespace allstarr.Tests;

public sealed class InjectedPlaylistTrackActionsContractTests
{
    [Fact]
    public void InjectedTrackModal_ExposesAccessiblePerTrackMatchActions()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("aria-haspopup=\"menu\"", script, StringComparison.Ordinal);
        Assert.Contains(">Match</button>", script, StringComparison.Ordinal);
        Assert.Contains("Local library", script, StringComparison.Ordinal);
        Assert.Contains("Music providers", script, StringComparison.Ordinal);
        Assert.Contains(">Search</button>", script, StringComparison.Ordinal);
        Assert.Contains("Clear match", script, StringComparison.Ordinal);
        Assert.Contains(".track-action-popover", styles, StringComparison.Ordinal);
        Assert.Contains(".track-match-editor", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedTrackActions_UseTheProviderNeutralMappingBoundary()
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
    public void ClearingDurableMapping_InvalidatesDerivedPlaylistCaches()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "MappingController.cs"));

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

        Assert.Contains("[\"externalTracks\"] = 0", controller, StringComparison.Ordinal);
        Assert.Contains("playlistInfo[\"externalTracks\"] = coverage.External", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyPlaylistStats(playlistInfo, coverage.Local, coverage.External, coverage.Missing)", controller, StringComparison.Ordinal);
        Assert.Contains("display(playlist.externalTracks)", script, StringComparison.Ordinal);
        Assert.Contains("ResolveCanonicalPlaylistCoverageAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("PlaylistSummarySchemaVersion = 9", controller, StringComparison.Ordinal);
        Assert.Equal(2, Count(controller, "GetSourcePlaylistTracksAsync(") - 1);
        Assert.Contains("BuildSpotifyMissingTracksKey(playlistName)", controller, StringComparison.Ordinal);
        Assert.Contains("PlaylistCoverageMath.Normalize(", controller, StringComparison.Ordinal);
        Assert.Contains("[\"providerBreakdown\"]", controller, StringComparison.Ordinal);
        Assert.Contains("class=\"playlist-coverage\"", script, StringComparison.Ordinal);
        Assert.Contains("providerCoverageColor(", script, StringComparison.Ordinal);
        Assert.Contains("Math.min(trackCount, Math.max(0", script, StringComparison.Ordinal);
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
        Assert.Contains("lastSuccessfulSyncAt", controller, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyPlaylistLastSuccessfulSyncKey", controller, StringComparison.Ordinal);
        Assert.Contains("playlistMetadata?.FetchedAt", controller, StringComparison.Ordinal);
        Assert.Contains("nextSyncAt", controller, StringComparison.Ordinal);
        Assert.Contains("matchStatus", controller, StringComparison.Ordinal);
        Assert.Contains("Local</small>", script, StringComparison.Ordinal);
        Assert.Contains("External</small>", script, StringComparison.Ordinal);
        Assert.Contains("Unmatched</small>", script, StringComparison.Ordinal);
        Assert.Contains("Next rematch", script, StringComparison.Ordinal);
        Assert.Contains("Last synced", script, StringComparison.Ordinal);
        Assert.Contains("<span>Sync now</span>", script, StringComparison.Ordinal);
        Assert.Contains("playlist-rematch-action", script, StringComparison.Ordinal);
        var summaryStart = script.IndexOf("playlist-operation-summary\" aria-label=\"Playlist synchronization details", StringComparison.Ordinal);
        var summaryEnd = script.IndexOf("</div>", summaryStart, StringComparison.Ordinal);
        var action = script.IndexOf("playlist-rematch-action", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart && action > summaryEnd,
            "The rematch action must be a sibling of the synchronization stat strip, not nested inside it.");
        Assert.Contains("Current source snapshot needs matching", script, StringComparison.Ordinal);
        Assert.Contains(".playlist-operation-summary", styles, StringComparison.Ordinal);
        Assert.Contains(".playlist-operation-actions button", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistDetails_UseContiguousDisplayOrderAndRetainRawProviderPosition()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "PlaylistController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.True(
            controller.Split("position = trackIndex + 1", StringSplitOptions.None).Length >= 3,
            "Both playlist detail paths must return a contiguous display ordinal.");
        Assert.True(
            controller.Split("sourcePosition = track.Position", StringSplitOptions.None).Length >= 3,
            "Both playlist detail paths must retain the raw provider position for diagnostics.");
        Assert.Contains("Provider position ${track.sourcePosition}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPlaylistRows_AreWholeRowInteractiveWithoutHijackingControls()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("class=\"injected-table-row-interactive\"", script, StringComparison.Ordinal);
        Assert.Contains("button, input, details, summary, a, select", script, StringComparison.Ordinal);
        Assert.Contains("injected-heading-actions", script, StringComparison.Ordinal);
        Assert.Contains(".injected-data-table tbody tr:hover td", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncActions_RefreshSourcesAndRunMatching()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("matchAllPlaylists: () =>", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/playlists/match-all", script, StringComparison.Ordinal);
        Assert.Contains("await API.refreshPlaylist(name);", script, StringComparison.Ordinal);
        Assert.Contains("await API.matchPlaylist(name);", script, StringComparison.Ordinal);
        Assert.Contains("await API.refreshPlaylists();", script, StringComparison.Ordinal);
        Assert.Contains("await API.matchAllPlaylists();", script, StringComparison.Ordinal);
        Assert.Contains("Refreshed and rematched", script, StringComparison.Ordinal);
        Assert.Contains("Playlist rematching queued. Progress appears in the operation center.", script, StringComparison.Ordinal);

        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "PlaylistController.cs"));
        Assert.Contains("DurableJobQueue jobs", controller, StringComparison.Ordinal);
        Assert.Contains("\"playlist.match-all\"", controller, StringComparison.Ordinal);
        Assert.Contains("return Accepted", controller, StringComparison.Ordinal);

        var matcher = File.ReadAllText(FindRepositoryFile("allstarr", "Core", "Matching", "PlaylistMatchingCoordinator.cs"));
        var adapter = File.ReadAllText(FindRepositoryFile("allstarr", "Services", "Spotify", "SpotifyPlaylistMatchingAdapter.cs"));
        Assert.Contains("PlaylistMatchAllJobHandler", matcher, StringComparison.Ordinal);
        Assert.Contains("BuildSpotifyMissingTracksKey(playlist.Name)", adapter, StringComparison.Ordinal);
        Assert.Contains("sourceTracks is { Count: > 0 }", adapter, StringComparison.Ordinal);
        Assert.Contains("MatchPlaylistTracksWithIsrcAsync(", adapter, StringComparison.Ordinal);
        Assert.Contains("sourceTracks?.OrderBy(track => track.Position)", adapter, StringComparison.Ordinal);
        Assert.Contains("IDurableJobHandler", matcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTrackRows_OpenAnAuthoritativeMappingHistoryDialog()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "TrackMatchesController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("[HttpGet(\"spotify/{spotifyId}\")]", controller, StringComparison.Ordinal);
        Assert.Contains("ITrackMatchRepository trackMatchCommands", controller, StringComparison.Ordinal);
        Assert.Contains("trackMatchCommands.GetDetailAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("detail.ProviderIdentities", controller, StringComparison.Ordinal);
        Assert.Contains("detail.Snapshots", controller, StringComparison.Ordinal);
        Assert.Contains("detail.Artifacts", controller, StringComparison.Ordinal);
        Assert.Contains("source = \"materialized Jellyfin playlist\"", controller, StringComparison.Ordinal);
        Assert.Contains("trackMappingDetails:", script, StringComparison.Ordinal);
        Assert.Contains("backendItemId", script, StringComparison.Ordinal);
        Assert.Contains("Open mapping details for", script, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"track-details-dialog\"", script, StringComparison.Ordinal);
        Assert.Contains("<small>Playback</small>", script, StringComparison.Ordinal);
        Assert.Contains("<h4>Current route</h4>", script, StringComparison.Ordinal);
        Assert.Contains("<h4>Known services</h4>", script, StringComparison.Ordinal);
        Assert.Contains("Technical history", script, StringComparison.Ordinal);
        Assert.Contains("<h4>Recent activity</h4>", script, StringComparison.Ordinal);
        Assert.Contains("compact-track-details", script, StringComparison.Ordinal);
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
