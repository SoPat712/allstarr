using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Models.Admin;
using allstarr.Services.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Services;
using allstarr.Filters;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using allstarr.Core.Settings;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using Cronos;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class PlaylistController : ControllerBase
{
    private readonly ILogger<PlaylistController> _logger;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly SpotifyPlaylistFetcher _playlistFetcher;
    private readonly IPlaylistMatchingCoordinator? _matchingService;
    private readonly ITrackMatchRepository _trackMatchCommands;
    private readonly IApplicationCache _cache;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private const string CacheDirectory = "/app/cache/spotify";
    private const int PlaylistSummarySchemaVersion = 9;

    public PlaylistController(
        ILogger<PlaylistController> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        SpotifyPlaylistFetcher playlistFetcher,
        ITrackMatchRepository trackMatchCommands,
        IApplicationCache cache,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        AdminHelperService helperService,
        IServiceProvider serviceProvider,
        IPlaylistMatchingCoordinator? matchingService = null)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _playlistFetcher = playlistFetcher;
        _matchingService = matchingService;
        _trackMatchCommands = trackMatchCommands;
        _cache = cache;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
        _helperService = helperService;
        _serviceProvider = serviceProvider;
    }

    [HttpGet("playlists")]
    public async Task<IActionResult> GetPlaylists([FromQuery] bool refresh = false)
    {
        var playlistSummaryKey = CacheKeyBuilder.BuildAdminPlaylistSummaryKey();
        // Version 3 owns playlist configuration in the tenant's durable settings.
        // Reading the store directly also avoids waiting for the in-memory projector.
        var configuredPlaylists = await GetConfiguredPlaylistsAsync();

        // Check the shared cache first unless refresh is requested.
        if (!refresh)
        {
            try
            {
                var cachedJson = await _cache.GetStringAsync(playlistSummaryKey);
                if (!string.IsNullOrWhiteSpace(cachedJson))
                {
                    using var cachedDocument = JsonDocument.Parse(cachedJson);
                    var cachedNames = cachedDocument.RootElement.TryGetProperty("playlists", out var cachedPlaylists) &&
                                      cachedPlaylists.ValueKind == JsonValueKind.Array
                        ? cachedPlaylists.EnumerateArray()
                            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : [];
                    var currentSummaryShape = cachedDocument.RootElement.TryGetProperty("schemaVersion", out var cachedSchemaVersion) &&
                                              cachedSchemaVersion.GetInt32() == PlaylistSummarySchemaVersion &&
                                              cachedPlaylists.ValueKind == JsonValueKind.Array &&
                                              cachedPlaylists.EnumerateArray().All(item =>
                                                  item.TryGetProperty("artworkUrl", out _) &&
                                                  item.TryGetProperty("artworkSource", out _) &&
                                                  item.TryGetProperty("matchedTracks", out _) &&
                                                  item.TryGetProperty("providerBreakdown", out _) &&
                                                  item.TryGetProperty("syncStatus", out _));
                    currentSummaryShape = currentSummaryShape &&
                                          cachedDocument.RootElement.TryGetProperty("inventory", out _);
                    if (currentSummaryShape &&
                        cachedNames.Count == configuredPlaylists.Count &&
                        configuredPlaylists.All(item => cachedNames.Contains(item.Name)))
                    {
                        var cachedData = JsonSerializer.Deserialize<Dictionary<string, object>>(cachedJson);
                        _logger.LogDebug("Returning cached playlist summary");
                        return Ok(cachedData);
                    }
                    _logger.LogDebug("Playlist configuration changed after the summary was cached; rebuilding it");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read cached playlist summary");
            }
        }
        else if (refresh)
        {
            await _cache.DeleteAsync(playlistSummaryKey);
            _logger.LogDebug("Force refresh requested for playlist summary");
        }

        var playlists = new List<object>();

        var targetBackend = (_configuration.GetValue<string>("Backend:Type") ?? "Jellyfin")
            .Trim()
            .ToLowerInvariant();

        foreach (var config in configuredPlaylists)
        {
            var playlistInfo = new Dictionary<string, object?>
            {
                ["name"] = config.Name,
                ["id"] = config.Id,
                ["jellyfinId"] = config.JellyfinId,
                ["localTracksPosition"] = config.LocalTracksPosition.ToString(),
                ["syncSchedule"] = config.SyncSchedule ?? "0 8 * * *",
                ["trackCount"] = 0,
                ["localTracks"] = 0,
                ["externalTracks"] = 0,
                ["lastFetched"] = null as DateTime?,
                ["lastSuccessfulSyncAt"] = await ResolveLastSuccessfulSyncAtAsync(config.Name),
                ["cacheAge"] = null as string,
                ["artworkUrl"] = null as string,
                ["providerBreakdown"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                ["sourceProvider"] = "spotify"
            };

            try
            {
                var sourceTracks = await GetSourcePlaylistTracksAsync(config.Name);
                var playlistMetadata = await _playlistFetcher.GetPlaylistMetadataAsync(config.Name);
                playlistInfo["trackCount"] = sourceTracks.Count;
                var artworkUrl = playlistMetadata?.ImageUrl ?? sourceTracks.FirstOrDefault()?.AlbumArtUrl;
                if (string.IsNullOrWhiteSpace(artworkUrl) &&
                    targetBackend == "jellyfin" &&
                    !string.IsNullOrWhiteSpace(config.JellyfinId))
                {
                    artworkUrl = $"/Items/{Uri.EscapeDataString(config.JellyfinId)}/Images/Primary";
                }
                playlistInfo["artworkUrl"] = artworkUrl;
                playlistInfo["artworkSource"] = !string.IsNullOrWhiteSpace(playlistMetadata?.ImageUrl)
                    ? "playlist"
                    : !string.IsNullOrWhiteSpace(sourceTracks.FirstOrDefault()?.AlbumArtUrl)
                        ? "track_fallback"
                        : "target";
                playlistInfo["lastFetched"] = playlistMetadata?.FetchedAt;
                if (playlistMetadata?.FetchedAt is { } fetchedAt)
                {
                    var age = DateTime.UtcNow - fetchedAt;
                    playlistInfo["cacheAge"] = age.TotalHours < 1
                        ? $"{age.TotalMinutes:F0}m"
                        : $"{age.TotalHours:F1}h";
                }

                var coverage = await ResolveCanonicalPlaylistCoverageAsync(config.Name, sourceTracks);
                ApplyPlaylistStats(playlistInfo, coverage.Local, coverage.External, coverage.Missing);
                playlistInfo["providerBreakdown"] = coverage.ProviderBreakdown;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build playlist summary for {Playlist}", config.Name);
            }

            EnrichPlaylistSummary(playlistInfo, config.SyncSchedule);
            playlists.Add(playlistInfo);
        }

        var inventory = await GetPlaylistInventoryAsync(configuredPlaylists);

        var response = new { schemaVersion = PlaylistSummarySchemaVersion, playlists, inventory };

        // Cache the reconstructable summary for five minutes.
        try
        {
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
            await _cache.SetStringAsync(playlistSummaryKey, json, TimeSpan.FromMinutes(5));
            _logger.LogDebug("Saved playlist summary to shared cache");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist summary cache");
        }

        return Ok(response);
    }

    private sealed record PlaylistCoverage(
        int Local,
        int External,
        int Missing,
        Dictionary<string, int> ProviderBreakdown);

    private async Task<List<SpotifyPlaylistTrack>> GetSourcePlaylistTracksAsync(string playlistName)
    {
        var tracks = await _playlistFetcher.GetPlaylistTracksAsync(playlistName);
        if (tracks.Count > 0)
        {
            return tracks;
        }

        var retained = await _cache.GetAsync<List<MissingTrack>>(
            CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName));
        return retained?.Select((track, position) => new SpotifyPlaylistTrack
        {
            SpotifyId = track.SpotifyId,
            Position = position,
            Title = track.Title,
            Album = track.Album,
            Artists = track.Artists,
            AlbumArtUrl = track.AlbumArtUrl,
            DurationMs = track.DurationMs,
            Isrc = track.Isrc
        }).ToList() ?? [];
    }

    private async Task<PlaylistCoverage> ResolveCanonicalPlaylistCoverageAsync(
        string playlistName,
        IReadOnlyList<SpotifyPlaylistTrack> sourceTracks)
    {
        var targetBackend = (_configuration.GetValue<string>("Backend:Type") ?? "Jellyfin")
            .Trim()
            .ToLowerInvariant();
        var matchedTracksBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase);
        var cachedItemsBySpotifyId = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(
            CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName));
        foreach (var matched in matchedTracks ?? [])
        {
            if (!string.IsNullOrWhiteSpace(matched.SpotifyId) &&
                matched.MatchedSong != null &&
                !matchedTracksBySpotifyId.ContainsKey(matched.SpotifyId))
            {
                matchedTracksBySpotifyId[matched.SpotifyId] = matched;
            }
        }

        var cachedItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(
            CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName));
        foreach (var item in cachedItems ?? [])
        {
            var providerIds = ReadCachedProviderIds(item);
            if (providerIds?.TryGetValue("Spotify", out var spotifyId) == true &&
                !string.IsNullOrWhiteSpace(spotifyId))
            {
                cachedItemsBySpotifyId[spotifyId] = item;
            }
        }

        var materializedItems = await GetMaterializedPlaylistItemsAsync(playlistName);
        var materializedItemsBySpotifyId = MatchMaterializedItems(sourceTracks, materializedItems);
        var providerBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var local = 0;
        var external = 0;

        foreach (var track in sourceTracks)
        {
            materializedItemsBySpotifyId.TryGetValue(track.SpotifyId, out var item);
            item ??= cachedItemsBySpotifyId.GetValueOrDefault(track.SpotifyId);

            bool? isLocal = null;
            string? externalProvider = null;
            var itemId = item == null ? null : ReadCachedString(item, "Id");
            var providerIds = item == null ? null : ReadCachedProviderIds(item);
            if (item != null)
            {
                externalProvider = providerIds == null
                    ? null
                    : ResolveExternalProviderFromProviderIds(providerIds);
                if (IsExternalPlaylistItem(item) || !string.IsNullOrWhiteSpace(externalProvider))
                {
                    isLocal = false;
                    externalProvider ??= ExtractExternalProviderFromItemId(itemId);
                }
                else
                {
                    isLocal = true;
                }
            }

            var projection = await _trackMatchCommands.GetSpotifyProjectionAsync(track.SpotifyId);
            if (isLocal == false && string.IsNullOrWhiteSpace(externalProvider))
            {
                if (PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                        matchedTracksBySpotifyId,
                        track.SpotifyId,
                        out var resolvedIsLocal,
                        out var resolvedExternalProvider) &&
                    resolvedIsLocal == false)
                {
                    externalProvider = resolvedExternalProvider;
                }
                else if (projection.ProviderRoutes.FirstOrDefault() is { } route)
                {
                    externalProvider = route.ProviderId;
                }
            }
            else if (isLocal == null && !string.IsNullOrWhiteSpace(projection.LocalBackendItemId))
            {
                isLocal = true;
            }
            else if (isLocal == null && projection.ProviderRoutes.FirstOrDefault() is { } durableRoute)
            {
                isLocal = false;
                externalProvider = durableRoute.ProviderId;
            }
            else if (isLocal == null &&
                     PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                         matchedTracksBySpotifyId,
                         track.SpotifyId,
                         out var resolvedIsLocal,
                         out var resolvedExternalProvider))
            {
                isLocal = resolvedIsLocal;
                externalProvider = resolvedExternalProvider;
            }

            if (isLocal == false)
            {
                externalProvider = NormalizeExternalProviderForDisplay(externalProvider);
                if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(externalProvider, itemId))
                {
                    isLocal = null;
                    externalProvider = null;
                }
            }

            if (isLocal == true)
            {
                local++;
                IncrementProviderCount(providerBreakdown, targetBackend);
            }
            else if (isLocal == false)
            {
                external++;
                IncrementProviderCount(providerBreakdown, externalProvider ?? "external");
            }
        }

        return new PlaylistCoverage(
            local,
            external,
            Math.Max(0, sourceTracks.Count - local - external),
            providerBreakdown);
    }

    private static void IncrementProviderCount(Dictionary<string, int> counts, string provider)
    {
        counts[provider] = counts.GetValueOrDefault(provider) + 1;
    }

    private async Task<Dictionary<string, int>> GetPlaylistInventoryAsync(
        IReadOnlyCollection<SpotifyPlaylistConfig> configuredPlaylists)
    {
        var managed = configuredPlaylists.Count;
        try
        {
            var userId = _jellyfinSettings.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                var usersRequest = _helperService.CreateJellyfinRequest(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users");
                using var usersResponse = await _jellyfinHttpClient.SendAsync(usersRequest, HttpContext.RequestAborted);
                if (usersResponse.IsSuccessStatusCode)
                {
                    using var usersDocument = JsonDocument.Parse(
                        await usersResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted));
                    userId = usersDocument.RootElement.ValueKind == JsonValueKind.Array &&
                             usersDocument.RootElement.GetArrayLength() > 0
                        ? usersDocument.RootElement[0].GetProperty("Id").GetString()
                        : null;
                }
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return new Dictionary<string, int>
                {
                    ["managed"] = managed,
                    ["unmanaged"] = 0,
                    ["total"] = managed
                };
            }

            var request = _helperService.CreateJellyfinRequest(
                HttpMethod.Get,
                $"{_jellyfinSettings.Url}/Users/{userId}/Items?IncludeItemTypes=Playlist&Recursive=true&Limit=10000");
            using var response = await _jellyfinHttpClient.SendAsync(request, HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(HttpContext.RequestAborted));
            var managedIds = configuredPlaylists
                .Select(item => item.JellyfinId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var backendPlaylistIds = document.RootElement.TryGetProperty("Items", out var items) &&
                                     items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray()
                    .Select(item => item.TryGetProperty("Id", out var id) ? id.GetString() : null)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var unmanaged = backendPlaylistIds.Count(id => !managedIds.Contains(id));
            return new Dictionary<string, int>
            {
                ["managed"] = managed,
                ["unmanaged"] = unmanaged,
                ["total"] = managed + unmanaged
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load media-server playlist inventory");
            return new Dictionary<string, int>
            {
                ["managed"] = managed,
                ["unmanaged"] = 0,
                ["total"] = managed
            };
        }
    }

    private static void ApplyPlaylistStats(
        Dictionary<string, object?> playlistInfo,
        int local,
        int external,
        int missing)
    {
        var coverage = PlaylistCoverageMath.Normalize(
            ReadSummaryInt(playlistInfo, "trackCount"),
            local,
            external,
            missing);
        playlistInfo["localTracks"] = coverage.Local;
        playlistInfo["externalTracks"] = coverage.External;
        playlistInfo["externalMatched"] = coverage.External;
        playlistInfo["externalMissing"] = coverage.Missing;
        playlistInfo["externalTotal"] = coverage.External + coverage.Missing;
        playlistInfo["totalInJellyfin"] = coverage.Playable;
        playlistInfo["totalPlayable"] = coverage.Playable;
    }

    private static void EnrichPlaylistSummary(
        Dictionary<string, object?> playlistInfo,
        string? syncSchedule)
    {
        var trackCount = ReadSummaryInt(playlistInfo, "trackCount");
        var matchedTracks = Math.Clamp(
            ReadSummaryInt(playlistInfo, "totalPlayable"),
            0,
            trackCount);
        var unmatchedTracks = Math.Max(0, trackCount - matchedTracks);
        var matchPercent = trackCount > 0
            ? Math.Round(matchedTracks * 100d / trackCount, 1)
            : 0d;
        var lastSyncAt = playlistInfo.TryGetValue("lastSuccessfulSyncAt", out var completed)
            ? completed switch
            {
                DateTime value => value,
                DateTimeOffset value => value.UtcDateTime,
                JsonElement { ValueKind: JsonValueKind.String } element when element.TryGetDateTime(out var value) => value,
                _ => (DateTime?)null
            }
            : null;

        DateTime? nextSyncAt = null;
        if (!string.IsNullOrWhiteSpace(syncSchedule))
        {
            try
            {
                var cron = CronExpression.Parse(syncSchedule);
                nextSyncAt = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
            }
            catch (CronFormatException)
            {
                // The existing configuration validator reports invalid schedules. The summary
                // remains readable while an operator corrects an older imported value.
            }
        }

        playlistInfo["matchedTracks"] = matchedTracks;
        playlistInfo["unmatchedTracks"] = unmatchedTracks;
        playlistInfo["matchPercent"] = matchPercent;
        playlistInfo["syncStatus"] = trackCount <= 0
            ? "pending"
            : unmatchedTracks == 0
                ? "synced"
                : matchedTracks == 0
                    ? "needs_matching"
                    : "partial";
        playlistInfo["lastSyncAt"] = lastSyncAt;
        playlistInfo["lastSourceRefreshAt"] = playlistInfo.TryGetValue("lastFetched", out var sourceRefresh)
            ? sourceRefresh
            : null;
        playlistInfo["nextSyncAt"] = nextSyncAt;
    }

    private static int ReadSummaryInt(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value == null)
        {
            return 0;
        }

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            _ when int.TryParse(value.ToString(), out var number) => number,
            _ => 0
        };
    }

    private static string? ReadCachedString(Dictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value.ToString()
        };
    }

    private static Dictionary<string, string>? ReadCachedProviderIds(Dictionary<string, object?> item)
    {
        if (!item.TryGetValue("ProviderIds", out var value) || value == null)
        {
            return null;
        }

        if (value is Dictionary<string, string> providerIds)
        {
            return new Dictionary<string, string>(providerIds, StringComparer.OrdinalIgnoreCase);
        }

        if (value is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? "",
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Dictionary<string, object?>> MatchMaterializedItems(
        IReadOnlyList<SpotifyPlaylistTrack> sourceTracks,
        IReadOnlyList<Dictionary<string, object?>>? materializedItems)
    {
        var matches = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        if (materializedItems == null || materializedItems.Count == 0)
        {
            return matches;
        }

        var remaining = materializedItems.Select((item, index) => (item, index)).ToList();
        foreach (var track in sourceTracks)
        {
            var direct = remaining.FirstOrDefault(candidate =>
                ReadCachedProviderIds(candidate.item)?.TryGetValue("Spotify", out var spotifyId) == true &&
                spotifyId.Equals(track.SpotifyId, StringComparison.OrdinalIgnoreCase));
            var matchIndex = direct.item == null
                ? remaining.FindIndex(candidate => MaterializedIdentityMatches(track, candidate.item))
                : remaining.FindIndex(candidate => candidate.index == direct.index);
            if (matchIndex < 0)
            {
                continue;
            }

            matches[track.SpotifyId] = remaining[matchIndex].item;
            remaining.RemoveAt(matchIndex);
        }

        return matches;
    }

    private static bool MaterializedIdentityMatches(
        SpotifyPlaylistTrack source,
        Dictionary<string, object?> item)
    {
        var itemArtists = ReadCachedStringList(item, "Artists");
        if (itemArtists.Count == 0)
        {
            var albumArtist = ReadCachedString(item, "AlbumArtist");
            if (!string.IsNullOrWhiteSpace(albumArtist))
            {
                itemArtists.Add(albumArtist);
            }
        }

        return PlaylistTrackStatusResolver.MaterializedIdentityMatches(
            source.Title,
            source.PrimaryArtist,
            ReadCachedString(item, "Name"),
            itemArtists);
    }

    private static List<string> ReadCachedStringList(Dictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return [];
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.Where(entry => !string.IsNullOrWhiteSpace(entry)).ToList();
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            return array.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString() ?? "")
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToList();
        }

        return [];
    }

    private static bool IsExternalPlaylistItem(Dictionary<string, object?> item)
    {
        var itemId = ReadCachedString(item, "Id");
        var serverId = ReadCachedString(item, "ServerId");
        return string.Equals(serverId, "allstarr", StringComparison.OrdinalIgnoreCase) ||
               itemId?.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<List<Dictionary<string, object?>>?> GetMaterializedPlaylistItemsAsync(string playlistName)
    {
        var playlist = (await GetConfiguredPlaylistsAsync()).FirstOrDefault(item =>
            item.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.JellyfinId))
        {
            return null;
        }

        var userId = _jellyfinSettings.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            var usersRequest = _helperService.CreateJellyfinRequest(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users");
            using var usersResponse = await _jellyfinHttpClient.SendAsync(usersRequest, HttpContext.RequestAborted);
            if (usersResponse.IsSuccessStatusCode)
            {
                using var usersDocument = JsonDocument.Parse(await usersResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted));
                userId = usersDocument.RootElement.ValueKind == JsonValueKind.Array &&
                         usersDocument.RootElement.GetArrayLength() > 0
                    ? usersDocument.RootElement[0].GetProperty("Id").GetString()
                    : null;
            }
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var url = $"{_jellyfinSettings.Url}/Playlists/{playlist.JellyfinId}/Items?UserId={userId}&Fields=ProviderIds,Path";
        var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);
        using var response = await _jellyfinHttpClient.SendAsync(request, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to read materialized playlist {Playlist} while building track details: {StatusCode}",
                playlistName,
                response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(HttpContext.RequestAborted));
        if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return items.EnumerateArray()
            .Select(item => JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText()))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();
    }

    /// <summary>
    /// Get tracks for a specific playlist with local/external status
    /// </summary>
    [HttpGet("playlists/{name}/tracks")]
    public async Task<IActionResult> GetPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        var playlistConfig = (await GetConfiguredPlaylistsAsync()).FirstOrDefault(item =>
            item.Name.Equals(decodedName, StringComparison.OrdinalIgnoreCase));

        // Get Spotify tracks
        var spotifyTracks = await GetSourcePlaylistTracksAsync(decodedName);

        var tracksWithStatus = new List<object>();
        var matchedTrackCount = 0;
        var localTrackCount = 0;
        var externalTrackCount = 0;
        var providerBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var targetBackend = (_configuration.GetValue<string>("Backend:Type") ?? "Jellyfin").ToLowerInvariant();
        var playlistMetadata = await _playlistFetcher.GetPlaylistMetadataAsync(decodedName);
        var playlistArtworkUrl = playlistMetadata?.ImageUrl ?? spotifyTracks.FirstOrDefault()?.AlbumArtUrl;
        if (string.IsNullOrWhiteSpace(playlistArtworkUrl) &&
            targetBackend == "jellyfin" &&
            !string.IsNullOrWhiteSpace(playlistConfig?.JellyfinId))
        {
            playlistArtworkUrl = $"/Items/{Uri.EscapeDataString(playlistConfig.JellyfinId)}/Images/Primary";
        }
        var playlistArtworkSource = !string.IsNullOrWhiteSpace(playlistMetadata?.ImageUrl)
            ? "playlist"
            : !string.IsNullOrWhiteSpace(spotifyTracks.FirstOrDefault()?.AlbumArtUrl)
                ? "track_fallback"
                : "target";
        var syncSchedule = playlistConfig?.SyncSchedule ?? "0 8 * * *";
        var lastSourceRefreshAt = playlistMetadata?.FetchedAt ?? ReadPlaylistCacheTimestamp(decodedName);
        var lastSuccessfulSyncAt = await ResolveLastSuccessfulSyncAtAsync(decodedName);
        var nextSyncAt = GetNextScheduledOccurrence(syncSchedule);
        var matchedTracksBySpotifyId = new Dictionary<string, MatchedTrack>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(decodedName);
            var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);

            if (matchedTracks != null)
            {
                foreach (var matched in matchedTracks)
                {
                    if (string.IsNullOrWhiteSpace(matched.SpotifyId) || matched.MatchedSong == null)
                    {
                        continue;
                    }

                    if (!matchedTracksBySpotifyId.ContainsKey(matched.SpotifyId))
                    {
                        matchedTracksBySpotifyId[matched.SpotifyId] = matched;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load matched tracks cache for {Playlist}", decodedName);
        }

        // Use the pre-built playlist cache (same as GetPlaylists endpoint)
        // This cache includes all matched tracks with proper provider IDs
        var playlistItemsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(decodedName);

        List<Dictionary<string, object?>>? cachedPlaylistItems = null;
        try
        {
            cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsCacheKey);
        }
        catch (Exception cacheEx)
        {
            _logger.LogWarning(cacheEx, "Failed to deserialize playlist cache for {Playlist}", decodedName);
        }

        var materializedItemsBySpotifyId = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        // The materialized Jellyfin playlist is authoritative only when an item can be joined
        // to the current provider snapshot by identity. Position is not an identity for rotating
        // playlists such as Release Radar.
        try
        {
            var materializedItems = await GetMaterializedPlaylistItemsAsync(decodedName);
            materializedItemsBySpotifyId = MatchMaterializedItems(spotifyTracks, materializedItems);
            _logger.LogDebug(
                "Matched {MatchedCount} of {SourceCount} current tracks to materialized Jellyfin items in {Playlist}",
                materializedItemsBySpotifyId.Count,
                spotifyTracks.Count,
                decodedName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load materialized track details for {Playlist}", decodedName);
        }

        _logger.LogDebug("GetPlaylistTracks for {Playlist}: Cache found: {Found}, Count: {Count}",
            decodedName, cachedPlaylistItems != null, cachedPlaylistItems?.Count ?? 0);

        if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
        {
            // Build a map of Spotify ID -> cached item for quick lookup
            var spotifyIdToItem = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var item in cachedPlaylistItems)
            {
                // Try to get Spotify ID from ProviderIds (works for both local and external)
                var providerIds = ReadCachedProviderIds(item);
                if (providerIds != null)
                {
                    if (providerIds.TryGetValue("Spotify", out var spotifyId) && !string.IsNullOrEmpty(spotifyId))
                    {
                        spotifyIdToItem[spotifyId] = item;
                    }
                }
            }

            // Match each source track to its materialized item. Modern caches include the
            // source identity; upgraded caches may only preserve the ordered Jellyfin items.
            // When both ordered collections have the same length, the materialized playlist
            // is authoritative and position is a safe compatibility fallback.
            for (var trackIndex = 0; trackIndex < spotifyTracks.Count; trackIndex++)
            {
                var track = spotifyTracks[trackIndex];
                bool? isLocal = null;
                string? externalProvider = null;
                bool isManualMapping = false;
                string? manualMappingType = null;
                string? manualMappingId = null;

                Dictionary<string, object?>? cachedItem = null;

                // Try to match by Spotify ID only (no position-based fallback!)
                if (materializedItemsBySpotifyId.TryGetValue(track.SpotifyId, out cachedItem))
                {
                    _logger.LogDebug("Matched track {Title} to current materialized Jellyfin identity", track.Title);
                }
                else if (spotifyIdToItem.TryGetValue(track.SpotifyId, out cachedItem))
                {
                    _logger.LogDebug("Matched track {Title} by Spotify ID", track.Title);
                }

                // Check if track is in the playlist cache first
                if (cachedItem != null)
                {
                    // Synthetic tracks now use the proxied Jellyfin server identity so clients
                    // resolve artwork correctly. The ext- item ID is the durable discriminator;
                    // retain the old ServerId check for caches created by earlier releases.
                    if (cachedItem.TryGetValue("ServerId", out var serverIdObj) && serverIdObj != null)
                    {
                        string? serverId = null;
                        if (serverIdObj is string str)
                        {
                            serverId = str;
                        }
                        else if (serverIdObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.String)
                        {
                            serverId = jsonEl.GetString();
                        }

                        var cachedItemId = cachedItem.TryGetValue("Id", out var idValue)
                            ? idValue switch
                            {
                                string value => value,
                                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                                _ => null
                            }
                            : null;
                        if (serverId == "allstarr" ||
                            cachedItemId?.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // This is an external track stub
                            isLocal = false;

                            // Try to determine the provider from ProviderIds
                            var providerIdsExt = ReadCachedProviderIds(cachedItem);
                            if (providerIdsExt != null)
                            {
                                externalProvider = ResolveExternalProviderFromProviderIds(providerIdsExt);
                            }

                            // Fallback 1: derive provider from matched-track cache
                            if (string.IsNullOrWhiteSpace(externalProvider) &&
                                PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                                    matchedTracksBySpotifyId,
                                    track.SpotifyId,
                                    out var resolvedIsLocal,
                                    out var resolvedExternalProvider) &&
                                resolvedIsLocal == false)
                            {
                                externalProvider = NormalizeExternalProviderForDisplay(resolvedExternalProvider);
                            }

                            // Fallback 2: derive provider from the durable PostgreSQL projection.
                            var cachedDurableProjection = await _trackMatchCommands.GetSpotifyProjectionAsync(track.SpotifyId);
                            if (string.IsNullOrWhiteSpace(externalProvider) &&
                                cachedDurableProjection.ProviderRoutes.FirstOrDefault() is { } route)
                            {
                                externalProvider = route.ProviderId;
                            }

                            // Fallback 3: derive provider from external item ID prefix (ext-{provider}-...)
                            if (string.IsNullOrWhiteSpace(externalProvider) &&
                                cachedItem.TryGetValue("Id", out var cachedItemIdObj))
                            {
                                var externalItemId = cachedItemIdObj switch
                                {
                                    string s => s,
                                    JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                                    _ => null
                                };

                                externalProvider = ExtractExternalProviderFromItemId(externalItemId);
                            }

                            _logger.LogDebug("✓ Track {Title} identified as external synthetic item (provider: {Provider})",
                                track.Title, externalProvider ?? "unknown");

                            // Check if this is a manual mapping
                            if (cachedDurableProjection.ProviderRoutes.Any(route => route.IsManual))
                            {
                                isManualMapping = true;
                                manualMappingType = "external";
                                manualMappingId = cachedDurableProjection.ProviderRoutes
                                    .First(route => route.IsManual).ExternalId;
                            }

                            // Skip the rest of the ProviderIds logic
                            goto AddTrack;
                        }
                    }

                    // Track is in the playlist cache with real Jellyfin ServerId - determine type from ProviderIds
                    var providerIds = ReadCachedProviderIds(cachedItem);
                    if (providerIds != null)
                    {
                        _logger.LogDebug("Track {Title} has ProviderIds: {Keys}", track.Title, string.Join(", ", providerIds.Keys));

                        externalProvider = ResolveExternalProviderFromProviderIds(providerIds);

                        if (!string.IsNullOrWhiteSpace(externalProvider))
                        {
                            isLocal = false;
                            _logger.LogDebug("✓ Track {Title} identified as {Provider} from cache", track.Title, externalProvider);
                        }
                        else
                        {
                            // No external provider key found - it's a local Jellyfin track
                            isLocal = true;
                            _logger.LogDebug("✓ Track {Title} identified as LOCAL from cache", track.Title);
                        }
                    }
                    else
                    {
                        // Track is in cache but has NO ProviderIds - treat as local
                        isLocal = true;
                        _logger.LogDebug("✓ Track {Title} identified as LOCAL (in cache, no ProviderIds)", track.Title);
                    }

                    // Check if this is a manual mapping (for display purposes)
                    var durableProjection = await _trackMatchCommands.GetSpotifyProjectionAsync(track.SpotifyId);
                    if (durableProjection.IsManual)
                    {
                        isManualMapping = true;
                        manualMappingType = durableProjection.LocalIsManual ? "jellyfin" : "external";
                        manualMappingId = durableProjection.LocalIsManual
                            ? durableProjection.LocalBackendItemId
                            : durableProjection.ProviderRoutes.FirstOrDefault(route => route.IsManual)?.ExternalId;
                    }
                }
                else
                {
                    // Track NOT in playlist cache - check the durable manual decision.
                    var durableProjection = await _trackMatchCommands.GetSpotifyProjectionAsync(track.SpotifyId);

                    if (!string.IsNullOrWhiteSpace(durableProjection.LocalBackendItemId))
                    {
                        isLocal = true;
                        if (durableProjection.LocalIsManual)
                        {
                            isManualMapping = true;
                            manualMappingType = "jellyfin";
                            manualMappingId = durableProjection.LocalBackendItemId;
                        }
                    }
                    else if (durableProjection.ProviderRoutes.FirstOrDefault() is { } route)
                    {
                        isLocal = false;
                        externalProvider = route.ProviderId;
                        if (route.IsManual)
                        {
                            isManualMapping = true;
                            manualMappingType = "external";
                            manualMappingId = route.ExternalId;
                        }
                    }
                    else if (PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                                 matchedTracksBySpotifyId,
                                 track.SpotifyId,
                                 out var resolvedIsLocal,
                                 out var resolvedExternalProvider))
                    {
                        isLocal = resolvedIsLocal;
                        externalProvider = resolvedExternalProvider;
                    }
                    else
                    {
                        isLocal = null;
                        externalProvider = null;
                    }
                }

            AddTrack:
                if (isLocal == false)
                {
                    externalProvider = NormalizeExternalProviderForDisplay(externalProvider);
                    if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(externalProvider))
                    {
                        isLocal = null;
                        externalProvider = null;
                        isManualMapping = false;
                        manualMappingType = null;
                        manualMappingId = null;
                    }
                }

                // Check lyrics status
                var cacheKey = CacheKeyBuilder.BuildLyricsKey(
                    track.PrimaryArtist,
                    track.Title,
                    track.Album,
                    track.DurationMs / 1000);
                var existingLyrics = await _cache.GetStringAsync(cacheKey);
                var hasLyrics = !string.IsNullOrEmpty(existingLyrics);
                if (isLocal.HasValue)
                {
                    matchedTrackCount++;
                    if (isLocal.Value)
                    {
                        localTrackCount++;
                        IncrementProviderCount(providerBreakdown, targetBackend);
                    }
                    else
                    {
                        externalTrackCount++;
                        IncrementProviderCount(providerBreakdown, externalProvider ?? "external");
                    }
                }

                var backendItemId = isLocal == true
                    ? cachedItem != null ? ReadCachedString(cachedItem, "Id") : manualMappingId
                    : null;
                var albumArtUrl = track.AlbumArtUrl;
                if (string.IsNullOrWhiteSpace(albumArtUrl) &&
                    targetBackend == "jellyfin" &&
                    !string.IsNullOrWhiteSpace(backendItemId))
                {
                    albumArtUrl = $"/Items/{Uri.EscapeDataString(backendItemId)}/Images/Primary";
                }

                tracksWithStatus.Add(new
                {
                    position = trackIndex + 1,
                    sourcePosition = track.Position,
                    title = track.Title,
                    artists = track.Artists,
                    album = track.Album,
                    isrc = track.Isrc,
                    spotifyId = track.SpotifyId,
                    durationMs = track.DurationMs,
                    albumArtUrl,
                    backendItemId,
                    isLocal = isLocal,
                    externalProvider = externalProvider,
                    provider = isLocal == true ? targetBackend : externalProvider,
                    matchState = isLocal == true ? "local" : isLocal == false ? "external" : "unmatched",
                    searchQuery = isLocal != true ? $"{track.Title} {track.PrimaryArtist}" : null,
                    isManualMapping = isManualMapping,
                    manualMappingType = manualMappingType,
                    manualMappingId = manualMappingId,
                    hasLyrics = hasLyrics
                });
            }

            return Ok(new
            {
                name = decodedName,
                trackCount = spotifyTracks.Count,
                artworkUrl = playlistArtworkUrl,
                artworkSource = playlistArtworkSource,
                sourceProvider = "spotify",
                targetBackend,
                totalPlayable = matchedTrackCount,
                localTracks = localTrackCount,
                externalTracks = externalTrackCount,
                matchedTracks = matchedTrackCount,
                unmatchedTracks = Math.Max(0, spotifyTracks.Count - matchedTrackCount),
                providerBreakdown,
                syncSchedule,
                lastSourceRefreshAt,
                lastSuccessfulSyncAt,
                nextSyncAt,
                matchStatus = matchedTrackCount == spotifyTracks.Count
                    ? "ready"
                    : matchedTrackCount == 0
                        ? "rematch_required"
                        : "partial",
                tracks = tracksWithStatus
            });
        }

        // Fallback: Cache not available, use matched tracks cache
        _logger.LogDebug("Playlist cache not available for {Playlist}, using fallback", decodedName);

        for (var trackIndex = 0; trackIndex < spotifyTracks.Count; trackIndex++)
        {
            var track = spotifyTracks[trackIndex];
            bool? isLocal = null;
            string? externalProvider = null;
            string? backendItemId = null;

            if (materializedItemsBySpotifyId.TryGetValue(track.SpotifyId, out var materializedItem) &&
                !IsExternalPlaylistItem(materializedItem))
            {
                isLocal = true;
                backendItemId = ReadCachedString(materializedItem, "Id");
            }

            var durableProjection =
                await _trackMatchCommands.GetSpotifyProjectionAsync(track.SpotifyId);

            if (isLocal == true)
            {
                // The materialized backend playlist is authoritative for currently playable
                // local entries even when an upgraded cache no longer carries Spotify IDs.
            }
            else if (!string.IsNullOrWhiteSpace(durableProjection.LocalBackendItemId))
            {
                isLocal = true;
                backendItemId = durableProjection.LocalBackendItemId;
            }
            else if (durableProjection.ProviderRoutes.FirstOrDefault() is { } durableRoute)
            {
                isLocal = false;
                externalProvider = NormalizeExternalProviderForDisplay(durableRoute.ProviderId);
            }
            else
            {
                if (PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                             matchedTracksBySpotifyId,
                             track.SpotifyId,
                             out var resolvedIsLocal,
                             out var resolvedExternalProvider))
                {
                    isLocal = resolvedIsLocal;
                    externalProvider = resolvedExternalProvider;
                }
                else
                {
                    isLocal = null;
                    externalProvider = null;
                }
            }

            if (isLocal == false)
            {
                externalProvider = NormalizeExternalProviderForDisplay(externalProvider);
                if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(externalProvider))
                {
                    isLocal = null;
                    externalProvider = null;
                }
            }

            var albumArtUrl = track.AlbumArtUrl;
            if (string.IsNullOrWhiteSpace(albumArtUrl) &&
                targetBackend == "jellyfin" &&
                !string.IsNullOrWhiteSpace(backendItemId))
            {
                albumArtUrl = $"/Items/{Uri.EscapeDataString(backendItemId)}/Images/Primary";
            }

            tracksWithStatus.Add(new
            {
                position = trackIndex + 1,
                sourcePosition = track.Position,
                title = track.Title,
                artists = track.Artists,
                album = track.Album,
                isrc = track.Isrc,
                spotifyId = track.SpotifyId,
                durationMs = track.DurationMs,
                albumArtUrl,
                backendItemId,
                isLocal = isLocal,
                externalProvider = externalProvider,
                provider = isLocal == true ? targetBackend : externalProvider,
                matchState = isLocal == true ? "local" : isLocal == false ? "external" : "unmatched",
                searchQuery = isLocal != true ? $"{track.Title} {track.PrimaryArtist}" : null
            });
            if (isLocal.HasValue)
            {
                matchedTrackCount++;
                if (isLocal.Value)
                {
                    localTrackCount++;
                }
                else
                {
                    externalTrackCount++;
                }
            }
        }

        return Ok(new
        {
            name = decodedName,
            trackCount = spotifyTracks.Count,
            artworkUrl = playlistArtworkUrl,
            artworkSource = playlistArtworkSource,
            sourceProvider = "spotify",
            targetBackend,
            totalPlayable = matchedTrackCount,
            localTracks = localTrackCount,
            externalTracks = externalTrackCount,
            matchedTracks = matchedTrackCount,
            unmatchedTracks = Math.Max(0, spotifyTracks.Count - matchedTrackCount),
            syncSchedule,
            lastSourceRefreshAt,
            lastSuccessfulSyncAt,
            nextSyncAt,
            matchStatus = matchedTrackCount == spotifyTracks.Count
                ? "ready"
                : matchedTrackCount == 0
                    ? "rematch_required"
                    : "partial",
            tracks = tracksWithStatus
        });
    }

    private static DateTime? GetNextScheduledOccurrence(string? syncSchedule)
    {
        if (string.IsNullOrWhiteSpace(syncSchedule))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(syncSchedule).GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    private static DateTime? ReadPlaylistCacheTimestamp(string playlistName)
    {
        var cacheFilePath = Path.Combine(
            CacheDirectory,
            $"{AdminHelperService.SanitizeFileName(playlistName)}_spotify.json");
        if (!System.IO.File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(cacheFilePath));
            if (document.RootElement.TryGetProperty("fetchedAt", out var fetchedAt) &&
                fetchedAt.TryGetDateTime(out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Fall back to the file timestamp for older cache formats.
        }

        return System.IO.File.GetLastWriteTimeUtc(cacheFilePath);
    }

    private async Task<DateTime?> ResolveLastSuccessfulSyncAtAsync(string playlistName)
    {
        var cacheValue = await _cache.GetStringAsync(
            CacheKeyBuilder.BuildSpotifyPlaylistLastSuccessfulSyncKey(playlistName));
        if (DateTimeOffset.TryParse(cacheValue, out var completedAt))
        {
            return completedAt.UtcDateTime;
        }

        return null;
    }

    /// <summary>
    /// Trigger a manual refresh of all playlists
    /// </summary>
    [HttpPost("playlists/refresh")]
    public async Task<IActionResult> RefreshPlaylists()
    {
        _logger.LogInformation("Manual playlist refresh triggered from admin UI");
        await _playlistFetcher.TriggerFetchAsync();

        // Invalidate playlist summary cache
        await _cache.DeleteAsync(CacheKeyBuilder.BuildAdminPlaylistSummaryKey());

        // Clear ALL playlist stats caches
        var configuredPlaylists = await GetConfiguredPlaylistsAsync();
        foreach (var playlist in configuredPlaylists)
        {
            var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(playlist.Name);
            await _cache.DeleteAsync(statsCacheKey);
        }
        _logger.LogInformation("Cleared stats cache for all {Count} playlists", configuredPlaylists.Count);

        return Ok(new { message = "Playlist refresh triggered", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Refresh a single playlist from Spotify (fetch latest data without re-matching).
    /// </summary>
    [HttpPost("playlists/{name}/refresh")]
    public async Task<IActionResult> RefreshPlaylist(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Manual refresh triggered for playlist: {Name}", decodedName);

        if (_playlistFetcher == null)
        {
            return BadRequest(new { error = "Playlist fetcher is not available" });
        }

        try
        {
            await _playlistFetcher.RefreshPlaylistAsync(decodedName);

            // Clear playlist stats cache first (so it gets recalculated with fresh data)
            var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(decodedName);
            await _cache.DeleteAsync(statsCacheKey);

            // Then invalidate playlist summary cache (will rebuild with fresh stats)
            await _cache.DeleteAsync(CacheKeyBuilder.BuildAdminPlaylistSummaryKey());

            return Ok(new
            {
                message = $"Refreshed {decodedName} from Spotify (no re-matching)",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh playlist {Name}", decodedName);
            return StatusCode(500, new { error = "Failed to refresh playlist" });
        }
    }

    /// <summary>
    /// Re-match tracks when LOCAL library has changed (checks if Jellyfin playlist changed).
    /// This is a lightweight operation that reuses cached Spotify data.
    /// </summary>
    [HttpPost("playlists/{name}/match")]
    public async Task<IActionResult> MatchPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Re-match tracks triggered for playlist: {Name} (checking for local changes)", decodedName);

        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }

        try
        {
            // Clear the Jellyfin playlist signature cache to force re-checking if local tracks changed
            var jellyfinSignatureCacheKey =
                CacheKeyBuilder.BuildSpotifyPlaylistJellyfinSignatureKey(decodedName);
            await _cache.DeleteAsync(jellyfinSignatureCacheKey);
            _logger.LogDebug("Cleared Jellyfin signature cache to force change detection");

            // Clear the matched results cache to force re-matching
            var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(decodedName);
            await _cache.DeleteAsync(matchedTracksKey);
            _logger.LogDebug("Cleared matched tracks cache");

            // Clear the playlist items cache
            var playlistItemsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(decodedName);
            await _cache.DeleteAsync(playlistItemsCacheKey);
            _logger.LogDebug("Cleared playlist items cache");

            // Trigger matching (will use cached Spotify data if still valid)
            await _matchingService.TriggerMatchingForPlaylistAsync(decodedName);

            // Invalidate playlist summary cache
            await _cache.DeleteAsync(CacheKeyBuilder.BuildAdminPlaylistSummaryKey());

            // Clear playlist stats cache to force recalculation from new mappings
            var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(decodedName);
            await _cache.DeleteAsync(statsCacheKey);
            _logger.LogDebug("Cleared stats cache for {Name}", decodedName);

            return Ok(new
            {
                message = $"Re-matching tracks for {decodedName} (checking local changes)",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger track matching for {Name}", decodedName);
            return StatusCode(500, new { error = "Failed to trigger track matching" });
        }
    }

    /// <summary>
    /// Rebuild playlist from scratch when REMOTE (Spotify) playlist has changed.
    /// Clears all caches including Spotify data and forces fresh fetch.
    /// </summary>
    [HttpPost("playlists/{name}/clear-cache")]
    public async Task<IActionResult> ClearPlaylistCache(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Rebuild from scratch triggered for playlist: {Name}", decodedName);

        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }

        try
        {
            // Use the unified per-playlist rebuild method (same workflow as per-playlist cron rebuilds)
            await _matchingService.TriggerRebuildForPlaylistAsync(decodedName);

            // Invalidate playlist summary cache
            await _cache.DeleteAsync(CacheKeyBuilder.BuildAdminPlaylistSummaryKey());

            return Ok(new
            {
                message = $"Rebuilding {decodedName} from scratch",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild playlist {Name}", decodedName);
            return StatusCode(500, new { error = "Failed to rebuild playlist" });
        }
    }

    /// <summary>
    /// Search Jellyfin library for tracks (for manual mapping)
    /// </summary>
    [HttpGet("jellyfin/search")]
    public async Task<IActionResult> SearchJellyfinTracks([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required" });
        }

        try
        {
            var userId = _jellyfinSettings.UserId;

            // Build URL with UserId if available
            var url = $"{_jellyfinSettings.Url}/Items?searchTerm={Uri.EscapeDataString(query)}&includeItemTypes=Audio&recursive=true&limit=20";
            if (!string.IsNullOrEmpty(userId))
            {
                url += $"&UserId={userId}";
            }

            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);

            _logger.LogDebug("Searching Jellyfin: {Url}", url);

            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Jellyfin search failed: {StatusCode} - {Error}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to search Jellyfin" });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var tracks = new List<object>();
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    // Verify it's actually an Audio item
                    var type = item.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : "";
                    if (type != "Audio")
                    {
                        _logger.LogWarning("Skipping non-audio item: {Type}", type);
                        continue;
                    }

                    var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() : "";
                    var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : "";
                    var album = item.TryGetProperty("Album", out var albumEl) ? albumEl.GetString() : "";
                    var artist = "";

                    if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                    {
                        artist = artistsEl[0].GetString() ?? "";
                    }
                    else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                    {
                        artist = albumArtistEl.GetString() ?? "";
                    }

                    tracks.Add(new { id, name = title, title, artist, album });
                }
            }

            return Ok(new { tracks, results = tracks });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Jellyfin tracks");
            return StatusCode(500, new { error = "Search failed" });
        }
    }

    /// <summary>
    /// Search external provider tracks for manual mapping.
    /// </summary>
    [HttpGet("external/search")]
    public async Task<IActionResult> SearchExternalTracks(
        [FromQuery] string query,
        [FromQuery] string provider = "deezer",
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required" });
        }

        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedProvider == "apple-download")
        {
            normalizedProvider = "applemusic";
        }

        var extensionPlaybackProviders = HttpContext.RequestServices
            .GetService<ExtensionManager>()?
            .GetActiveExtensions()
            .Where(extension => extension.Types.Any(IsPlaybackCapability))
            .Select(extension => extension.Id.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .AsEnumerable();

        if (!IsSupportedExternalTrackProvider(normalizedProvider, extensionPlaybackProviders))
        {
            return BadRequest(new { error = $"{provider} is unsupported for this search" });
        }

        if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(normalizedProvider))
        {
            return BadRequest(new { error = $"{provider} is metadata-only and cannot be used as a playable track mapping" });
        }

        try
        {
            var metadataService = HttpContext.RequestServices.GetRequiredService<IMusicMetadataService>();
            var songs = await metadataService.SearchSongsAsync(
                query.Trim(),
                Math.Clamp(limit, 1, 50),
                HttpContext.RequestAborted);

            var results = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.ExternalId))
                .Where(s => string.Equals(s.ExternalProvider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.ExternalId!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(song => new
                {
                    id = song.ExternalId,
                    externalId = song.ExternalId,
                    title = song.Title,
                    artist = song.Artist,
                    album = song.Album,
                    externalProvider = song.ExternalProvider ?? normalizedProvider,
                    url = BuildExternalTrackUrl(song.ExternalProvider ?? normalizedProvider, song.ExternalId!)
                })
                .ToList();

            return Ok(new { results });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search external tracks for provider {Provider}", provider);
            return StatusCode(500, new { error = "Failed to search external tracks" });
        }
    }

    private static bool IsPlaybackCapability(string capability) =>
        capability.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
        capability.Equals("streaming", StringComparison.OrdinalIgnoreCase) ||
        capability.Equals("download", StringComparison.OrdinalIgnoreCase) ||
        capability.Equals("downloads", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedExternalTrackProvider(
        string provider,
        IEnumerable<string>? extensionProviderIds)
    {
        return provider is "deezer" or "qobuz" or "squidwtf" or "applemusic" or "apple-download" ||
               extensionProviderIds?.Contains(provider, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Search a specific external provider for playlists for the admin UI.
    /// </summary>
    [HttpGet("external/playlists/search")]
    public async Task<IActionResult> SearchExternalPlaylists(
        [FromQuery] string query,
        [FromQuery] string provider = "deezer",
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required" });
        }

        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsSupportedExternalPlaylistProvider(normalizedProvider))
        {
            return BadRequest(new { error = "Unsupported provider" });
        }

        try
        {
            var service = GetConcreteMetadataServiceByName(normalizedProvider);
            if (service == null)
            {
                return BadRequest(new { error = $"Provider '{normalizedProvider}' is not registered" });
            }

            var playlists = await service.SearchPlaylistsAsync(
                query.Trim(),
                Math.Clamp(limit, 1, 50),
                HttpContext.RequestAborted);

            var results = playlists
                .Where(p => !string.IsNullOrWhiteSpace(p.ExternalId))
                .Select(p => new
                {
                    id = p.Id,
                    externalId = p.ExternalId,
                    externalProvider = string.IsNullOrWhiteSpace(p.Provider) ? normalizedProvider : p.Provider,
                    name = p.Name,
                    description = p.Description,
                    curatorName = p.CuratorName,
                    trackCount = p.TrackCount,
                    duration = p.Duration,
                    coverUrl = p.CoverUrl
                })
                .ToList();

            return Ok(new { results });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search external playlists for provider {Provider}", provider);
            return StatusCode(500, new { error = "Failed to search external playlists" });
        }
    }

    /// <summary>
    /// Preview tracks from a specific external provider playlist.
    /// </summary>
    [HttpGet("external/playlists/{provider}/{externalId}/tracks")]
    public async Task<IActionResult> GetExternalPlaylistTracks(
        string provider,
        string externalId,
        [FromQuery] int limit = 50)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsSupportedExternalPlaylistProvider(normalizedProvider))
        {
            return BadRequest(new { error = "Unsupported provider" });
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            return BadRequest(new { error = "External playlist ID is required" });
        }

        try
        {
            var service = GetConcreteMetadataServiceByName(normalizedProvider);
            if (service == null)
            {
                return BadRequest(new { error = $"Provider '{normalizedProvider}' is not registered" });
            }

            var tracks = await service.GetPlaylistTracksAsync(
                normalizedProvider,
                externalId.Trim(),
                HttpContext.RequestAborted);

            var results = tracks
                .Take(Math.Clamp(limit, 1, 200))
                .Select(song => new
                {
                    id = song.Id,
                    externalId = song.ExternalId,
                    externalProvider = song.ExternalProvider ?? normalizedProvider,
                    title = song.Title,
                    artist = song.Artist,
                    album = song.Album,
                    duration = song.Duration,
                    isrc = song.Isrc
                })
                .ToList();

            return Ok(new { results, count = results.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to fetch tracks for external playlist {Provider}/{ExternalId}",
                provider,
                externalId);
            return StatusCode(500, new { error = "Failed to fetch external playlist tracks" });
        }
    }

    private static bool IsSupportedExternalPlaylistProvider(string provider) =>
        provider is "deezer" or "qobuz" or "squidwtf" or "applemusic";

    private IConcreteMetadataService? GetConcreteMetadataServiceByName(string provider)
    {
        var normalizedProvider = provider.ToLowerInvariant();
        var services = HttpContext.RequestServices.GetServices<IConcreteMetadataService>();

        return services.FirstOrDefault(s =>
            s.GetType().Name.StartsWith(normalizedProvider, StringComparison.OrdinalIgnoreCase) ||
            (normalizedProvider == "squidwtf" && s.GetType().Name.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase)) ||
            (normalizedProvider == "applemusic" && s.GetType().Name.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Get track details by Jellyfin ID (for URL-based mapping)
    /// </summary>
    [HttpGet("jellyfin/track/{id}")]
    public async Task<IActionResult> GetJellyfinTrack(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Track ID is required" });
        }

        try
        {
            var userId = _jellyfinSettings.UserId;

            var url = $"{_jellyfinSettings.Url}/Items/{id}";
            if (!string.IsNullOrEmpty(userId))
            {
                url += $"?UserId={userId}";
            }

            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);

            _logger.LogDebug("Fetching Jellyfin track {Id} from {Url}", id, url);

            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin track {Id}: {StatusCode} - {Error}",
                    id, response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Track not found in Jellyfin" });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var item = doc.RootElement;

            // Verify it's an Audio item
            var type = item.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : "";
            if (type != "Audio")
            {
                _logger.LogWarning("Item {Id} is not an Audio track, it's a {Type}", id, type);
                return BadRequest(new { error = $"Item is not an audio track (it's a {type})" });
            }

            var trackId = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() : "";
            var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : "";
            var album = item.TryGetProperty("Album", out var albumEl) ? albumEl.GetString() : "";
            var artist = "";

            if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
            {
                artist = artistsEl[0].GetString() ?? "";
            }
            else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
            {
                artist = albumArtistEl.GetString() ?? "";
            }

            _logger.LogInformation("Found Jellyfin track: {Title} by {Artist}", title, artist);

            return Ok(new
            {
                id = trackId,
                name = title,
                title,
                artist,
                album,
                track = new { id = trackId, name = title, title, artist, album }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Jellyfin track {Id}", id);
            return StatusCode(500, new { error = "Failed to get track details" });
        }
    }

    /// <summary>
    /// Save manual track mapping (local Jellyfin or external provider)
    /// </summary>
    [HttpPost("playlists/{name}/map")]
    public async Task<IActionResult> SaveManualMapping(string name, [FromBody] ManualMappingRequest request)
    {
        var decodedName = Uri.UnescapeDataString(name);

        if (string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return BadRequest(new { error = "SpotifyId is required" });
        }

        // Validate that either Jellyfin mapping or external mapping is provided
        var hasJellyfinMapping = !string.IsNullOrWhiteSpace(request.JellyfinId);
        var hasExternalMapping = !string.IsNullOrWhiteSpace(request.ExternalProvider) && !string.IsNullOrWhiteSpace(request.ExternalId);

        if (!hasJellyfinMapping && !hasExternalMapping)
        {
            return BadRequest(new { error = "Either JellyfinId or (ExternalProvider + ExternalId) is required" });
        }

        if (hasJellyfinMapping && hasExternalMapping)
        {
            return BadRequest(new { error = "Cannot specify both Jellyfin and external mapping for the same track" });
        }

        try
        {
            string? normalizedProvider = null;
            string? normalizedExternalId = null;

            if (!hasJellyfinMapping)
            {
                normalizedProvider = request.ExternalProvider!.ToLowerInvariant(); // Normalize to lowercase
                if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(normalizedProvider))
                {
                    return BadRequest(new
                    {
                        error = $"{request.ExternalProvider} is metadata-only and cannot be used as a playable track mapping"
                    });
                }

                normalizedExternalId = NormalizeExternalTrackId(normalizedProvider, request.ExternalId!);
            }

            if (!TrySession(out var session, out var sessionError)) return sessionError!;
            var resolution = await _trackMatchCommands.ResolveSpotifyAsync(
                new TrackMatchActor(
                    session!.TenantId!.Value,
                    session.AllstarrUserId!.Value,
                    session.IsAdministrator),
                request.SpotifyId,
                hasJellyfinMapping
                    ? new ResolveTrackMatchCommand(
                        "local",
                        BackendItemId: request.JellyfinId,
                        Reason: $"Selected from playlist {decodedName}")
                    : new ResolveTrackMatchCommand(
                        "provider",
                        ExternalProvider: normalizedProvider,
                        ExternalId: normalizedExternalId,
                        Reason: $"Selected from playlist {decodedName}"),
                HttpContext.TraceIdentifier,
                HttpContext.RequestAborted);
            if (!resolution.Succeeded)
            {
                return resolution.Failure switch
                {
                    TrackMatchCommandFailure.Invalid => BadRequest(new { error = resolution.Error }),
                    TrackMatchCommandFailure.NotFound => NotFound(new { error = resolution.Error }),
                    TrackMatchCommandFailure.Forbidden => StatusCode(403, new { error = resolution.Error }),
                    TrackMatchCommandFailure.Conflict => Conflict(new { error = resolution.Error }),
                    _ => StatusCode(500, new { error = resolution.Error ?? "Manual mapping could not be saved" })
                };
            }

            _logger.LogInformation(
                "Manual mapping saved: {Playlist} - Spotify {SpotifyId} → {TargetType} {TargetId}",
                decodedName,
                request.SpotifyId,
                hasJellyfinMapping ? "Jellyfin" : normalizedProvider,
                hasJellyfinMapping ? request.JellyfinId : normalizedExternalId);

            // Clear all related caches to force rebuild
            var matchedCacheKey =
                CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(decodedName);
            var orderedCacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(decodedName);
            var playlistItemsKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(decodedName);
            var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(decodedName);

            await _cache.DeleteAsync(matchedCacheKey);
            await _cache.DeleteAsync(orderedCacheKey);
            await _cache.DeleteAsync(playlistItemsKey);
            await _cache.DeleteAsync(statsCacheKey);

            _logger.LogInformation("Cleared playlist caches for {Playlist} to force rebuild", decodedName);

            // Fetch external provider track details to return to the UI (only for external mappings)
            string? trackTitle = null;
            string? trackArtist = null;
            string? trackAlbum = null;

            if (hasExternalMapping && normalizedProvider != null)
            {
                try
                {
                    var metadataService = HttpContext.RequestServices.GetRequiredService<IMusicMetadataService>();
                    var externalSong = await metadataService.GetSongAsync(normalizedProvider, normalizedExternalId!);

                    if (externalSong != null)
                    {
                        trackTitle = externalSong.Title;
                        trackArtist = externalSong.Artist;
                        trackAlbum = externalSong.Album;
                        _logger.LogInformation("✓ Fetched external track metadata: {Title} by {Artist}", trackTitle, trackArtist);
                    }
                    else
                    {
                        _logger.LogError("Failed to fetch external track metadata for {Provider} ID {Id}",
                            normalizedProvider, normalizedExternalId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch external track metadata, but mapping was saved");
                }
            }

            // Trigger immediate playlist rebuild with the new mapping
            if (_matchingService != null)
            {
                _logger.LogInformation("Triggering immediate playlist rebuild for {Playlist} with new manual mapping", decodedName);

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromMinutes(2));
                    await _matchingService.TriggerMatchingForPlaylistAsync(decodedName).WaitAsync(cts.Token);
                    _logger.LogInformation("✓ Playlist {Playlist} rebuilt successfully with manual mapping", decodedName);
                }
                catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogWarning("Playlist rebuild for {Playlist} timed out after 2 minutes", decodedName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to rebuild playlist {Playlist} after manual mapping", decodedName);
                }
            }
            else
            {
                _logger.LogWarning("Matching service not available - playlist will rebuild on next scheduled run");
            }

            if (hasJellyfinMapping)
            {
                return Ok(new
                {
                    message = "Mapping saved and playlist rebuild triggered",
                    track = new
                    {
                        id = request.JellyfinId,
                        isLocal = true
                    },
                    rebuildTriggered = _matchingService != null
                });
            }

            // Return success with track details if available
            var mappedTrack = new
            {
                id = normalizedExternalId ?? request.ExternalId,
                title = trackTitle ?? "Unknown",
                artist = trackArtist ?? "Unknown",
                album = trackAlbum ?? "Unknown",
                isLocal = false,
                externalProvider = normalizedProvider ?? request.ExternalProvider?.ToLowerInvariant() ?? "unknown"
            };

            return Ok(new
            {
                message = "Mapping saved and playlist rebuild triggered",
                track = mappedTrack,
                rebuildTriggered = _matchingService != null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual mapping");
            return StatusCode(500, new { error = "Failed to save mapping" });
        }
    }

    /// <summary>
    /// Trigger track matching for all playlists
    /// </summary>
    [HttpPost("playlists/match-all")]
    public async Task<IActionResult> MatchAllPlaylistTracks(
        [FromServices] DurableJobQueue jobs,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manual track matching triggered for all playlists");

        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }

        if (!TrySession(out var session, out var error)) return error!;
        var generation = DateTimeOffset.UtcNow.UtcTicks;
        var receipt = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMatchAllJobPayload>(
            "playlist.match-all",
            $"playlist-match-all:{session!.TenantId:N}:{generation / TimeSpan.TicksPerMinute}",
            new(generation),
            session.TenantId,
            session.AllstarrUserId,
            CorrelationId: HttpContext.TraceIdentifier), cancellationToken);
        return Accepted(new
        {
            message = receipt.Created ? "Playlist rematching queued" : "Playlist rematching is already queued",
            jobId = receipt.JobId,
            created = receipt.Created,
            generation
        });
    }

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession authenticated)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }
        if (!authenticated.TenantId.HasValue || !authenticated.AllstarrUserId.HasValue)
        {
            error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" });
            return false;
        }
        session = authenticated;
        return true;
    }

    private static string? NormalizeKnownExternalProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "squidwtf" or "squid-wtf" or "squid_wtf" or "tidal" => "squidwtf",
            "deezer" => "deezer",
            "qobuz" => "qobuz",
            "applemusic" or "apple-music" or "apple_music" => "applemusic",
            _ => null
        };
    }

    private static string? NormalizeExternalProviderForDisplay(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return NormalizeKnownExternalProvider(provider) ?? provider.Trim().ToLowerInvariant();
    }

    private static string? ResolveExternalProviderFromProviderIds(Dictionary<string, string> providerIds)
    {
        foreach (var providerKey in providerIds.Keys)
        {
            var normalized = NormalizeKnownExternalProvider(providerKey);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? ExtractExternalProviderFromItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var trimmed = itemId.Trim();
        if (!trimmed.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = trimmed.Split('-', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return NormalizeExternalProviderForDisplay(parts[1]);
    }

    private static string BuildExternalTrackUrl(string provider, string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return string.Empty;
        }

        return provider.ToLowerInvariant() switch
        {
            "squidwtf" => $"https://www.tidal.com/track/{externalId}",
            "deezer" => $"https://www.deezer.com/track/{externalId}",
            "qobuz" => $"https://open.qobuz.com/track/{externalId}",
            "applemusic" => $"https://music.apple.com/us/song/{externalId}",
            _ => externalId
        };
    }

    private static string NormalizeExternalTrackId(string provider, string externalId)
    {
        var normalizedProvider = (provider ?? string.Empty).ToLowerInvariant();
        var trimmed = (externalId ?? string.Empty).Trim();

        if (normalizedProvider != "squidwtf" || string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        if (trimmed.All(char.IsDigit))
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var queryId = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)
            .TryGetValue("id", out var values)
            ? values.FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(queryId) && queryId.All(char.IsDigit))
        {
            return queryId;
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment.All(char.IsDigit))
        {
            return lastSegment;
        }

        return trimmed;
    }

    private string? ResolvePreferredExternalProvider(SpotifyTrackMapping mapping)
    {
        if (mapping.TryGetExternalTarget(null, out var provider, out _))
        {
            return NormalizeExternalProviderForDisplay(provider);
        }

        return NormalizeExternalProviderForDisplay(mapping.ExternalProvider);
    }

    /// <summary>
    /// Rebuild all playlists from scratch (clear cache, fetch fresh data, re-match).
    /// This is a manual bulk action across all playlists - used by "Rebuild All Remote" button.
    /// </summary>
    [HttpPost("playlists/rebuild-all")]
    public async Task<IActionResult> RebuildAllPlaylists()
    {
        _logger.LogInformation("Manual full rebuild triggered for all playlists");

        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }

        try
        {
            await _matchingService.TriggerRebuildAllAsync();
            return Ok(new { message = "Full rebuild triggered for all playlists", timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger full rebuild for all playlists");
            return StatusCode(500, new { error = "Failed to trigger full rebuild" });
        }
    }

    /// <summary>
    /// Get current configuration (safe values only)
    /// </summary>
    [HttpPost("playlists")]
    public async Task<IActionResult> AddPlaylist([FromBody] AddPlaylistRequest request)
    {
        if (string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.SpotifyId))
        {
            return BadRequest(new { error = "Name and SpotifyId are required" });
        }

        _logger.LogInformation("Adding playlist: {Name} ({SpotifyId})", request.Name, request.SpotifyId);

        var currentPlaylists = await GetConfiguredPlaylistsAsync();

        // Check for duplicates
        if (currentPlaylists.Any(p => p.Id == request.SpotifyId || p.Name == request.Name))
        {
            return BadRequest(new { error = "Playlist with this name or ID already exists" });
        }

        // Add new playlist
        currentPlaylists.Add(new SpotifyPlaylistConfig
        {
            Name = request.Name,
            Id = request.SpotifyId,
            LocalTracksPosition = request.LocalTracksPosition == "last"
                ? LocalTracksPosition.Last
                : LocalTracksPosition.First
        });

        var playlistsJson = AdminHelperService.SerializePlaylistsForEnv(currentPlaylists);

        return await PersistConfiguredPlaylistsAsync(currentPlaylists, playlistsJson);
    }

    /// <summary>
    /// Remove a playlist from the configuration
    /// </summary>
    [HttpDelete("playlists/{name}")]
    public async Task<IActionResult> RemovePlaylist(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Removing playlist: {Name}", decodedName);

        var currentPlaylists = await GetConfiguredPlaylistsAsync();
        var playlist = currentPlaylists.FirstOrDefault(p => p.Name == decodedName);

        if (playlist == null)
        {
            return NotFound(new { error = "Playlist not found" });
        }

        currentPlaylists.Remove(playlist);

        var playlistsJson = AdminHelperService.SerializePlaylistsForEnv(currentPlaylists);

        return await PersistConfiguredPlaylistsAsync(currentPlaylists, playlistsJson);
    }

    /// <summary>
    /// Updates a playlist sync schedule independently of the selected media backend.
    /// </summary>
    [HttpPut("playlists/{name}/schedule")]
    public async Task<IActionResult> UpdatePlaylistSchedule(
        string name,
        [FromBody] UpdateScheduleRequest request)
    {
        var decodedName = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(request.SyncSchedule))
        {
            return BadRequest(new { error = "SyncSchedule is required" });
        }

        var cronParts = request.SyncSchedule.Trim().Split(
            new[] { ' ' },
            StringSplitOptions.RemoveEmptyEntries);
        if (cronParts.Length != 5)
        {
            return BadRequest(new
            {
                error = "Invalid cron format. Expected: minute hour day month dayofweek"
            });
        }

        var currentPlaylists = await GetConfiguredPlaylistsAsync();
        var playlist = currentPlaylists.FirstOrDefault(item =>
            item.Name.Equals(decodedName, StringComparison.OrdinalIgnoreCase));
        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{decodedName}' not found" });
        }

        playlist.SyncSchedule = request.SyncSchedule.Trim();
        var playlistsJson = AdminHelperService.SerializePlaylistsForEnv(currentPlaylists);
        return await PersistConfiguredPlaylistsAsync(currentPlaylists, playlistsJson);
    }

    private AdminAuthSession? GetAdminSession() =>
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession
            : null;

    private async Task<List<SpotifyPlaylistConfig>> GetConfiguredPlaylistsAsync()
    {
        var session = GetAdminSession();
        var settings = HttpContext.RequestServices.GetService<IDurableRuntimeSettings>();
        if (session?.TenantId is not { } tenantId || settings == null)
        {
            return _spotifyImportSettings.Playlists.ToList();
        }

        var current = await settings.GetAsync(tenantId, "SpotifyImport:Playlists", HttpContext.RequestAborted);
        return SpotifyPlaylistConfigParser.Parse((string)current.Value);
    }

    private async Task<IActionResult> PersistConfiguredPlaylistsAsync(
        IReadOnlyList<SpotifyPlaylistConfig> playlists,
        string playlistsJson)
    {
        var session = GetAdminSession();
        if (session?.TenantId is not { } tenantId)
        {
            return BadRequest(new { error = "The administrator session is not linked to an Allstarr tenant." });
        }

        var settings = HttpContext.RequestServices.GetRequiredService<IDurableRuntimeSettings>();
        var current = await settings.GetAsync(tenantId, "SpotifyImport:Playlists", HttpContext.RequestAborted);
        var result = await settings.ApplyBatchAsync(
            tenantId,
            [new RuntimeSettingWrite(
                "SpotifyImport:Playlists",
                playlistsJson,
                current.Origin == RuntimeSettingOrigin.Durable ? current.Revision : null)],
            "admin-ui",
            session.AllstarrUserId,
            HttpContext.RequestAborted);

        _spotifyImportSettings.Playlists = playlists.ToList();
        await _cache.DeleteAsync(CacheKeyBuilder.BuildAdminPlaylistSummaryKey());
        return Ok(new { message = "Playlist configuration updated.", changeVersion = result.ChangeVersion });
    }


    /// <summary>
    /// Save lyrics mapping to file for persistence across restarts.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
}
