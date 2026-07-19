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
    private readonly SpotifyTrackMatchingService? _matchingService;
    private readonly SpotifyMappingService _mappingService;
    private readonly RedisCacheService _cache;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private const string CacheDirectory = "/app/cache/spotify";

    public PlaylistController(
        ILogger<PlaylistController> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        SpotifyPlaylistFetcher playlistFetcher,
        SpotifyMappingService mappingService,
        RedisCacheService cache,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        AdminHelperService helperService,
        IServiceProvider serviceProvider,
        SpotifyTrackMatchingService? matchingService = null)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _playlistFetcher = playlistFetcher;
        _matchingService = matchingService;
        _mappingService = mappingService;
        _cache = cache;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
        _helperService = helperService;
        _serviceProvider = serviceProvider;
    }

    [HttpGet("playlists")]
    public async Task<IActionResult> GetPlaylists([FromQuery] bool refresh = false)
    {
        var playlistCacheFile = "/app/cache/admin_playlists_summary.json";
        // Version 3 owns playlist configuration in the tenant's durable settings.
        // Reading the store directly also avoids waiting for the in-memory projector.
        var configuredPlaylists = await GetConfiguredPlaylistsAsync();

        // Check file cache first (5 minute TTL) unless refresh is requested
        if (!refresh && System.IO.File.Exists(playlistCacheFile))
        {
            try
            {
                var fileInfo = new FileInfo(playlistCacheFile);
                var age = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;

                if (age.TotalMinutes < 5)
                {
                    var cachedJson = await System.IO.File.ReadAllTextAsync(playlistCacheFile);
                    using var cachedDocument = JsonDocument.Parse(cachedJson);
                    var cachedNames = cachedDocument.RootElement.TryGetProperty("playlists", out var cachedPlaylists) &&
                                      cachedPlaylists.ValueKind == JsonValueKind.Array
                        ? cachedPlaylists.EnumerateArray()
                            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : [];
                    var currentSummaryShape = cachedPlaylists.ValueKind == JsonValueKind.Array &&
                                              cachedPlaylists.EnumerateArray().All(item =>
                                                  item.TryGetProperty("artworkUrl", out _) &&
                                                  item.TryGetProperty("matchedTracks", out _) &&
                                                  item.TryGetProperty("syncStatus", out _));
                    if (currentSummaryShape &&
                        cachedNames.Count == configuredPlaylists.Count &&
                        configuredPlaylists.All(item => cachedNames.Contains(item.Name)))
                    {
                        var cachedData = JsonSerializer.Deserialize<Dictionary<string, object>>(cachedJson);
                        _logger.LogDebug("📦 Returning cached playlist summary (age: {Age:F1}m)", age.TotalMinutes);
                        return Ok(cachedData);
                    }
                    _logger.LogDebug("Playlist configuration changed after the summary was cached; rebuilding it");
                }
                else
                {
                    _logger.LogWarning("🔄 Cache expired (age: {Age:F1}m), refreshing...", age.TotalMinutes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read cached playlist summary");
            }
        }
        else if (refresh)
        {
            _logger.LogDebug("🔄 Force refresh requested for playlist summary");
        }

        var playlists = new List<object>();

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
                ["cacheAge"] = null as string,
                ["artworkUrl"] = null as string,
                ["sourceProvider"] = "spotify"
            };

            // Get Spotify playlist track count from cache OR fetch it fresh
            var cacheFilePath = Path.Combine(CacheDirectory, $"{AdminHelperService.SanitizeFileName(config.Name)}_spotify.json");
            int spotifyTrackCount = 0;

            if (System.IO.File.Exists(cacheFilePath))
            {
                try
                {
                    var json = await System.IO.File.ReadAllTextAsync(cacheFilePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("tracks", out var tracks))
                    {
                        spotifyTrackCount = tracks.GetArrayLength();
                        playlistInfo["trackCount"] = spotifyTrackCount;
                        if (tracks.ValueKind == JsonValueKind.Array && tracks.GetArrayLength() > 0)
                        {
                            var firstTrack = tracks[0];
                            if (firstTrack.TryGetProperty("albumArtUrl", out var artwork) ||
                                firstTrack.TryGetProperty("AlbumArtUrl", out artwork))
                            {
                                playlistInfo["artworkUrl"] = artwork.GetString();
                            }
                        }
                    }

                    if (root.TryGetProperty("fetchedAt", out var fetchedAt))
                    {
                        var fetchedTime = fetchedAt.GetDateTime();
                        playlistInfo["lastFetched"] = fetchedTime;
                        var age = DateTime.UtcNow - fetchedTime;
                        playlistInfo["cacheAge"] = age.TotalHours < 1
                            ? $"{age.TotalMinutes:F0}m"
                            : $"{age.TotalHours:F1}h";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read cache for playlist {Name}", config.Name);
                }
            }

            // If cache doesn't exist or failed to read, fetch track count from Spotify API
            if (spotifyTrackCount == 0)
            {
                try
                {
                    var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(config.Name);
                    spotifyTrackCount = spotifyTracks.Count;
                    playlistInfo["trackCount"] = spotifyTrackCount;
                    playlistInfo["artworkUrl"] = spotifyTracks.FirstOrDefault()?.AlbumArtUrl;
                    _logger.LogDebug("Fetched {Count} tracks from Spotify for playlist {Name}", spotifyTrackCount, config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch Spotify track count for playlist {Name}", config.Name);
                }
            }

            // Calculate stats from playlist items cache (source of truth)
            // This is fast and always accurate
            var playlistItemStatsApplied = false;
            if (spotifyTrackCount > 0)
            {
                try
                {
                    // Try to use the pre-built playlist cache
                    var playlistItemsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(config.Name);

                    List<Dictionary<string, object?>>? cachedPlaylistItems = null;
                    try
                    {
                        cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsCacheKey);
                    }
                    catch (Exception cacheEx)
                    {
                        _logger.LogWarning(cacheEx, "Failed to deserialize playlist cache for {Playlist}", config.Name);
                    }

                    if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
                    {
                        // Calculate stats from the actual playlist cache
                        var localCount = 0;
                        var externalCount = 0;

                        foreach (var item in cachedPlaylistItems)
                        {
                            var serverId = ReadCachedString(item, "ServerId");
                            if (string.Equals(serverId, "allstarr", StringComparison.OrdinalIgnoreCase) ||
                                ReadCachedString(item, "Id")?.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                var providerIds = ReadCachedProviderIds(item);
                                var externalProvider = providerIds == null
                                    ? null
                                    : ResolveExternalProviderFromProviderIds(providerIds);
                                externalProvider ??= ExtractExternalProviderFromItemId(ReadCachedString(item, "Id"));

                                if (ExternalTrackPlaybackPolicy.CanUseForPlayback(
                                        externalProvider,
                                        ReadCachedString(item, "Id")))
                                {
                                    externalCount++;
                                }

                                continue;
                            }

                            localCount++;
                        }

                        var missingCount = spotifyTrackCount - (localCount + externalCount);

                        playlistInfo["localTracks"] = localCount;
                        playlistInfo["externalTracks"] = externalCount;
                        playlistInfo["externalMatched"] = externalCount;
                        playlistInfo["externalMissing"] = missingCount;
                        playlistInfo["externalTotal"] = externalCount + missingCount;
                        playlistInfo["totalInJellyfin"] = localCount + externalCount;
                        playlistInfo["totalPlayable"] = localCount + externalCount;

                        _logger.LogDebug("📊 Calculated stats from playlist cache for {Name}: {Local} local, {External} external, {Missing} missing",
                            config.Name, localCount, externalCount, missingCount);
                        playlistItemStatsApplied = true;
                    }
                    else
                    {
                        // No playlist cache - calculate from global mappings as fallback
                        var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(config.Name);
                        var localCount = 0;
                        var externalCount = 0;
                        var missingCount = 0;

                        foreach (var track in spotifyTracks)
                        {
                            var mapping = await _mappingService.GetMappingAsync(track.SpotifyId);

                            if (mapping != null)
                            {
                                if (mapping.TargetType == "local")
                                {
                                    localCount++;
                                }
                                else if (mapping.TargetType == "external")
                                {
                                    externalCount++;
                                }
                            }
                            else
                            {
                                missingCount++;
                            }
                        }

                        playlistInfo["localTracks"] = localCount;
                        playlistInfo["externalTracks"] = externalCount;
                        playlistInfo["externalMatched"] = externalCount;
                        playlistInfo["externalMissing"] = missingCount;
                        playlistInfo["externalTotal"] = externalCount + missingCount;
                        playlistInfo["totalInJellyfin"] = localCount + externalCount;
                        playlistInfo["totalPlayable"] = localCount + externalCount;

                        _logger.LogDebug("📊 Calculated stats from global mappings for {Name}: {Local} local, {External} external, {Missing} missing",
                            config.Name, localCount, externalCount, missingCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate playlist stats for {Name}", config.Name);
                }

                // Prefer the same matched-track records used by the detail modal. Serialized
                // Jellyfin item dictionaries and old aggregate caches can both outlive a provider
                // policy change, which previously let the overview drift from the track list.
                var matchedTrackStatsApplied = false;
                try
                {
                    var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(config.Name);
                    var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);
                    if (!playlistItemStatsApplied && matchedTracks != null)
                    {
                        var resolvedMatches = matchedTracks
                            .Where(match => !string.IsNullOrWhiteSpace(match.SpotifyId) && match.MatchedSong != null)
                            .GroupBy(match => match.SpotifyId, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First())
                            .ToList();
                        var canonicalLocal = resolvedMatches.Count(match => match.MatchedSong!.IsLocal);
                        var canonicalExternal = resolvedMatches.Count(match =>
                            !match.MatchedSong!.IsLocal &&
                            ExternalTrackPlaybackPolicy.CanUseForPlayback(
                                match.MatchedSong.ExternalProvider,
                                match.MatchedSong.Id));
                        var canonicalMissing = spotifyTrackCount - canonicalLocal - canonicalExternal;

                        if (canonicalMissing >= 0)
                        {
                            ApplyPlaylistStats(playlistInfo, canonicalLocal, canonicalExternal, canonicalMissing);
                            matchedTrackStatsApplied = true;
                        }
                    }

                    // Compatibility fallback for installations whose older cache did not persist
                    // MatchedTrack rows but did persist the matcher's aggregate result.
                    var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(config.Name);
                    var matchedStats = matchedTrackStatsApplied
                        ? null
                        : await _cache.GetAsync<Dictionary<string, int>>(statsCacheKey);
                    if (!playlistItemStatsApplied && !matchedTrackStatsApplied && matchedStats != null &&
                        matchedStats.TryGetValue("local", out var matchedLocal) &&
                        matchedStats.TryGetValue("external", out var matchedExternal) &&
                        matchedStats.TryGetValue("missing", out var matchedMissing) &&
                        matchedLocal >= 0 && matchedExternal >= 0 && matchedMissing >= 0 &&
                        matchedLocal + matchedExternal + matchedMissing == spotifyTrackCount)
                    {
                        ApplyPlaylistStats(playlistInfo, matchedLocal, matchedExternal, matchedMissing);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read canonical match stats for {Name}", config.Name);
                }

                // Durable global mappings are the final authority and survive upgrades even when
                // legacy per-playlist caches do not. This also runs every mapping through
                // SpotifyMappingService's playback-policy cleanup before it reaches the summary.
                try
                {
                    var canonicalTracks = playlistItemStatsApplied
                        ? []
                        : await _playlistFetcher.GetPlaylistTracksAsync(config.Name);
                    var canonicalLocal = 0;
                    var canonicalExternal = 0;

                    foreach (var track in canonicalTracks)
                    {
                        var mapping = await _mappingService.GetMappingAsync(track.SpotifyId);
                        if (mapping?.TargetType == "local")
                        {
                            canonicalLocal++;
                        }
                        else if (mapping?.TargetType == "external" &&
                                 mapping.TryGetExternalTarget(preferredProvider: null, out var provider, out var externalId) &&
                                 ExternalTrackPlaybackPolicy.CanUseForPlayback(provider, externalId))
                        {
                            canonicalExternal++;
                        }
                    }

                    if (!playlistItemStatsApplied && canonicalTracks.Count == spotifyTrackCount)
                    {
                        ApplyPlaylistStats(
                            playlistInfo,
                            canonicalLocal,
                            canonicalExternal,
                            spotifyTrackCount - canonicalLocal - canonicalExternal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to calculate durable mapping stats for {Name}", config.Name);
                }
            }

            // LEGACY FALLBACK: Only used if global mappings fail
            // This is the old slow path - kept for backwards compatibility
            if (!string.IsNullOrEmpty(config.JellyfinId) &&
                (int)(playlistInfo["totalPlayable"] ?? 0) == 0 &&
                spotifyTrackCount > 0)
            {
                try
                {
                    // Jellyfin requires UserId parameter to fetch playlist items
                    var userId = _jellyfinSettings.UserId;

                    // If no user configured, try to get the first user
                    if (string.IsNullOrEmpty(userId))
                    {
                        var usersRequest = _helperService.CreateJellyfinRequest(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users");
                        var usersResponse = await _jellyfinHttpClient.SendAsync(usersRequest);

                        if (usersResponse.IsSuccessStatusCode)
                        {
                            var usersJson = await usersResponse.Content.ReadAsStringAsync();
                            using var usersDoc = JsonDocument.Parse(usersJson);
                            if (usersDoc.RootElement.GetArrayLength() > 0)
                            {
                                userId = usersDoc.RootElement[0].GetProperty("Id").GetString();
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(userId))
                    {
                        _logger.LogWarning("No user ID available to fetch playlist items for {Name}", config.Name);
                    }
                    else
                    {
                        var url = $"{_jellyfinSettings.Url}/Playlists/{config.JellyfinId}/Items?UserId={userId}&Fields=Path";
                        var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);

                        _logger.LogDebug("Fetching Jellyfin playlist items for {Name} from {Url}", config.Name, url);

                        var response = await _jellyfinHttpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var jellyfinJson = await response.Content.ReadAsStringAsync();
                            using var jellyfinDoc = JsonDocument.Parse(jellyfinJson);

                            if (jellyfinDoc.RootElement.TryGetProperty("Items", out var items))
                            {
                                // Get Spotify tracks to match against
                                var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(config.Name);

                                // Try to use the pre-built playlist cache first (includes manual mappings!)
                                var playlistItemsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(config.Name);

                                List<Dictionary<string, object?>>? cachedPlaylistItems = null;
                                try
                                {
                                    cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsCacheKey);
                                }
                                catch (Exception cacheEx)
                                {
                                    _logger.LogWarning(cacheEx, "Failed to deserialize playlist cache for {Playlist}", config.Name);
                                }

                                _logger.LogDebug("Checking cache for {Playlist}: {CacheKey}, Found: {Found}, Count: {Count}",
                                    config.Name, playlistItemsCacheKey, cachedPlaylistItems != null, cachedPlaylistItems?.Count ?? 0);

                                if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
                                {
                                    // Use the pre-built cache which respects manual mappings
                                    // spotifyTracks already fetched above - reuse it
                                    var localCount = 0;
                                    var externalCount = 0;
                                    var missingCount = 0;

                                    // Count tracks by checking provider keys
                                    foreach (var item in cachedPlaylistItems)
                                    {
                                        if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                                        {
                                            Dictionary<string, string>? providerIds = null;

                                            if (providerIdsObj is Dictionary<string, string> dict)
                                            {
                                                providerIds = dict;
                                            }
                                            else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                                            {
                                                providerIds = new Dictionary<string, string>();
                                                foreach (var prop in jsonEl.EnumerateObject())
                                                {
                                                    providerIds[prop.Name] = prop.Value.GetString() ?? "";
                                                }
                                            }

                                            if (providerIds != null)
                                            {
                                                // Check if it's external (has squidwtf, deezer, qobuz, or tidal key)
                                                var hasSquidWTF = providerIds.ContainsKey("squidwtf");
                                                var hasDeezer = providerIds.ContainsKey("deezer");
                                                var hasQobuz = providerIds.ContainsKey("qobuz");
                                                var hasTidal = providerIds.ContainsKey("tidal");
                                                var isExternal = hasSquidWTF || hasDeezer || hasQobuz || hasTidal;

                                                if (isExternal)
                                                {
                                                    externalCount++;
                                                }
                                                else
                                                {
                                                    // Local track (has Jellyfin, MusicBrainz, or other metadata keys)
                                                    localCount++;
                                                }
                                            }
                                        }
                                    }

                                    // Calculate missing tracks: total Spotify tracks minus matched tracks
                                    // The playlist cache only contains successfully matched tracks (local + external)
                                    // So missing = total - (local + external)
                                    missingCount = spotifyTracks.Count - (localCount + externalCount);

                                    playlistInfo["localTracks"] = localCount;
                                    playlistInfo["externalTracks"] = externalCount;
                                    playlistInfo["externalMatched"] = externalCount;
                                    playlistInfo["externalMissing"] = missingCount;
                                    playlistInfo["externalTotal"] = externalCount + missingCount;
                                    playlistInfo["totalInJellyfin"] = localCount + externalCount; // Tracks actually in the Jellyfin playlist
                                    playlistInfo["totalPlayable"] = localCount + externalCount; // Total tracks that will be served

                                    _logger.LogDebug("Playlist {Name} (from cache): {Total} Spotify tracks, {Local} local, {ExtMatched} external matched, {ExtMissing} external missing, {Playable} total playable",
                                        config.Name, spotifyTracks.Count, localCount, externalCount, missingCount, localCount + externalCount);
                                }
                                else
                                {
                                    // Fallback: Build list of local tracks from Jellyfin (match by name only)
                                    var localTracks = new List<(string Title, string Artist)>();
                                    foreach (var item in items.EnumerateArray())
                                    {
                                        var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                                        var artist = "";

                                        if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                                        {
                                            artist = artistsEl[0].GetString() ?? "";
                                        }
                                        else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                                        {
                                            artist = albumArtistEl.GetString() ?? "";
                                        }

                                        if (!string.IsNullOrEmpty(title))
                                        {
                                            localTracks.Add((title, artist));
                                        }
                                    }

                                    // Get matched external tracks cache once
                                    var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(config.Name);
                                    var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);
                                    var matchedSpotifyIds = new HashSet<string>(
                                        matchedTracks?.Select(m => m.SpotifyId) ?? Enumerable.Empty<string>()
                                    );

                                    var localCount = 0;
                                    var externalMatchedCount = 0;
                                    var externalMissingCount = 0;

                                    // Match each Spotify track to determine if it's local, external, or missing
                                    foreach (var track in spotifyTracks)
                                    {
                                        var isLocal = false;
                                        var hasExternalMapping = false;

                                        // FIRST: Check for manual Jellyfin mapping
                                        var manualMappingKey = $"spotify:manual-map:{config.Name}:{track.SpotifyId}";
                                        var manualJellyfinId = await _cache.GetAsync<string>(manualMappingKey);

                                        if (!string.IsNullOrEmpty(manualJellyfinId))
                                        {
                                            // Manual Jellyfin mapping exists - this track is definitely local
                                            isLocal = true;
                                        }
                                        else
                                        {
                                            // Check for external manual mapping
                                            var externalMappingKey = $"spotify:external-map:{config.Name}:{track.SpotifyId}";
                                            var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);

                                            if (!string.IsNullOrEmpty(externalMappingJson))
                                            {
                                                // External manual mapping exists
                                                hasExternalMapping = true;
                                            }
                                            else if (localTracks.Count > 0)
                                            {
                                                // SECOND: No manual mapping, try fuzzy matching with local tracks
                                                var bestMatch = localTracks
                                                    .Select(local => new
                                                    {
                                                        Local = local,
                                                        TitleScore = FuzzyMatcher.CalculateSimilarity(track.Title, local.Title),
                                                        ArtistScore = FuzzyMatcher.CalculateSimilarity(track.PrimaryArtist, local.Artist)
                                                    })
                                                    .Select(x => new
                                                    {
                                                        x.Local,
                                                        x.TitleScore,
                                                        x.ArtistScore,
                                                        TotalScore = (x.TitleScore * 0.7) + (x.ArtistScore * 0.3)
                                                    })
                                                    .OrderByDescending(x => x.TotalScore)
                                                    .FirstOrDefault();

                                                // Use 70% threshold (same as playback matching)
                                                if (bestMatch != null && bestMatch.TotalScore >= 70)
                                                {
                                                    isLocal = true;
                                                }
                                            }
                                        }

                                        if (isLocal)
                                        {
                                            localCount++;
                                        }
                                        else
                                        {
                                            // Check if external track is matched (either manual mapping or auto-matched)
                                            if (hasExternalMapping || matchedSpotifyIds.Contains(track.SpotifyId))
                                            {
                                                externalMatchedCount++;
                                            }
                                            else
                                            {
                                                externalMissingCount++;
                                            }
                                        }
                                    }

                                    playlistInfo["localTracks"] = localCount;
                                    playlistInfo["externalTracks"] = externalMatchedCount;
                                    playlistInfo["externalMatched"] = externalMatchedCount;
                                    playlistInfo["externalMissing"] = externalMissingCount;
                                    playlistInfo["externalTotal"] = externalMatchedCount + externalMissingCount;
                                    playlistInfo["totalInJellyfin"] = localCount + externalMatchedCount;
                                    playlistInfo["totalPlayable"] = localCount + externalMatchedCount; // Total tracks that will be served

                                    _logger.LogWarning("Playlist {Name} (fallback): {Total} Spotify tracks, {Local} local, {ExtMatched} external matched, {ExtMissing} external missing, {Playable} total playable",
                                        config.Name, spotifyTracks.Count, localCount, externalMatchedCount, externalMissingCount, localCount + externalMatchedCount);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("No Items property in Jellyfin response for {Name}", config.Name);
                            }
                        }
                        else
                        {
                            _logger.LogError("Failed to get Jellyfin playlist {Name}: {StatusCode}",
                                config.Name, response.StatusCode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get Jellyfin playlist tracks for {Name}", config.Name);
                }
            }
            else
            {
                // Only log if JellyfinId is actually missing
                if (string.IsNullOrEmpty(config.JellyfinId))
                {
                    _logger.LogInformation("Playlist {Name} has no JellyfinId configured", config.Name);
                }
            }

            EnrichPlaylistSummary(playlistInfo, config.SyncSchedule);
            playlists.Add(playlistInfo);
        }

        // Save to file cache
        try
        {
            var cacheDir = "/app/cache";
            Directory.CreateDirectory(cacheDir);
            var cacheFile = Path.Combine(cacheDir, "admin_playlists_summary.json");

            var response = new { playlists };
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
            await System.IO.File.WriteAllTextAsync(cacheFile, json);

            _logger.LogDebug("💾 Saved playlist summary to cache");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist summary cache");
        }

        return Ok(new { playlists });
    }

    private static void ApplyPlaylistStats(
        Dictionary<string, object?> playlistInfo,
        int local,
        int external,
        int missing)
    {
        playlistInfo["localTracks"] = local;
        playlistInfo["externalTracks"] = external;
        playlistInfo["externalMatched"] = external;
        playlistInfo["externalMissing"] = missing;
        playlistInfo["externalTotal"] = external + missing;
        playlistInfo["totalInJellyfin"] = local + external;
        playlistInfo["totalPlayable"] = local + external;
    }

    private static void EnrichPlaylistSummary(
        Dictionary<string, object?> playlistInfo,
        string? syncSchedule)
    {
        var trackCount = ReadSummaryInt(playlistInfo, "trackCount");
        var matchedTracks = ReadSummaryInt(playlistInfo, "totalPlayable");
        var unmatchedTracks = Math.Max(0, trackCount - matchedTracks);
        var matchPercent = trackCount > 0
            ? Math.Round(matchedTracks * 100d / trackCount, 1)
            : 0d;
        var lastSyncAt = playlistInfo.TryGetValue("lastFetched", out var fetched)
            ? fetched as DateTime?
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
                : matchPercent >= 50d
                    ? "partial"
                    : "needs_attention";
        playlistInfo["lastSyncAt"] = lastSyncAt;
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
            return providerIds;
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

    /// <summary>
    /// Get tracks for a specific playlist with local/external status
    /// </summary>
    [HttpGet("playlists/{name}/tracks")]
    public async Task<IActionResult> GetPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);

        // Get Spotify tracks
        var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(decodedName);

        var tracksWithStatus = new List<object>();
        var matchedTrackCount = 0;
        var playlistArtworkUrl = spotifyTracks.FirstOrDefault()?.AlbumArtUrl;
        var targetBackend = (_configuration.GetValue<string>("Backend:Type") ?? "Jellyfin").ToLowerInvariant();
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

        _logger.LogDebug("GetPlaylistTracks for {Playlist}: Cache found: {Found}, Count: {Count}",
            decodedName, cachedPlaylistItems != null, cachedPlaylistItems?.Count ?? 0);

        if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
        {
            // Build a map of Spotify ID -> cached item for quick lookup
            var spotifyIdToItem = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var item in cachedPlaylistItems)
            {
                // Try to get Spotify ID from ProviderIds (works for both local and external)
                if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                {
                    Dictionary<string, string>? providerIds = null;

                    if (providerIdsObj is Dictionary<string, string> dict)
                    {
                        providerIds = dict;
                    }
                    else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                    {
                        providerIds = new Dictionary<string, string>();
                        foreach (var prop in jsonEl.EnumerateObject())
                        {
                            providerIds[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }

                    if (providerIds != null && providerIds.TryGetValue("Spotify", out var spotifyId) && !string.IsNullOrEmpty(spotifyId))
                    {
                        spotifyIdToItem[spotifyId] = item;
                    }
                }
            }

            // Match each Spotify track to its cached item
            foreach (var track in spotifyTracks)
            {
                bool? isLocal = null;
                string? externalProvider = null;
                bool isManualMapping = false;
                string? manualMappingType = null;
                string? manualMappingId = null;

                Dictionary<string, object?>? cachedItem = null;

                // Try to match by Spotify ID only (no position-based fallback!)
                if (spotifyIdToItem.TryGetValue(track.SpotifyId, out cachedItem))
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
                            if (cachedItem.TryGetValue("ProviderIds", out var providerIdsObjExt) && providerIdsObjExt != null)
                            {
                                Dictionary<string, string>? providerIdsExt = null;

                                if (providerIdsObjExt is Dictionary<string, string> dictExt)
                                {
                                    providerIdsExt = dictExt;
                                }
                                else if (providerIdsObjExt is JsonElement jsonElExt && jsonElExt.ValueKind == JsonValueKind.Object)
                                {
                                    providerIdsExt = new Dictionary<string, string>();
                                    foreach (var prop in jsonElExt.EnumerateObject())
                                    {
                                        providerIdsExt[prop.Name] = prop.Value.GetString() ?? "";
                                    }
                                }

                                if (providerIdsExt != null)
                                {
                                    externalProvider = ResolveExternalProviderFromProviderIds(providerIdsExt);
                                }
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

                            // Fallback 2: derive provider from global mapping
                            var globalMappingExt = await _mappingService.GetMappingAsync(track.SpotifyId);
                            if (string.IsNullOrWhiteSpace(externalProvider) &&
                                globalMappingExt?.TargetType == "external")
                            {
                                externalProvider = ResolvePreferredExternalProvider(globalMappingExt);
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
                            if (globalMappingExt != null && globalMappingExt.Source == "manual")
                            {
                                isManualMapping = true;
                                manualMappingType = "external";
                                manualMappingId = globalMappingExt.ExternalId;
                            }

                            // Skip the rest of the ProviderIds logic
                            goto AddTrack;
                        }
                    }

                    // Track is in the playlist cache with real Jellyfin ServerId - determine type from ProviderIds
                    if (cachedItem.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                    {
                        Dictionary<string, string>? providerIds = null;

                        if (providerIdsObj is Dictionary<string, string> dict)
                        {
                            providerIds = dict;
                        }
                        else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                        {
                            providerIds = new Dictionary<string, string>();
                            foreach (var prop in jsonEl.EnumerateObject())
                            {
                                providerIds[prop.Name] = prop.Value.GetString() ?? "";
                            }
                        }

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
                            isLocal = true;
                            _logger.LogDebug("✓ Track {Title} identified as LOCAL (ProviderIds null)", track.Title);
                        }
                    }
                    else
                    {
                        // Track is in cache but has NO ProviderIds - treat as local
                        isLocal = true;
                        _logger.LogDebug("✓ Track {Title} identified as LOCAL (in cache, no ProviderIds)", track.Title);
                    }

                    // Check if this is a manual mapping (for display purposes)
                    var globalMapping = await _mappingService.GetMappingAsync(track.SpotifyId);
                    if (globalMapping != null && globalMapping.Source == "manual")
                    {
                        isManualMapping = true;
                        manualMappingType = globalMapping.TargetType == "local" ? "jellyfin" : "external";
                        manualMappingId = globalMapping.TargetType == "local" ? globalMapping.LocalId : globalMapping.ExternalId;
                    }
                }
                else
                {
                    // Track NOT in playlist cache - check if there's a MANUAL global mapping
                    var globalMapping = await _mappingService.GetMappingAsync(track.SpotifyId);

                    if (globalMapping != null && globalMapping.Source == "manual")
                    {
                        // Manual mapping exists - trust it even if not in cache yet
                        _logger.LogDebug("✓ Track {Title} has MANUAL global mapping: {Type}", track.Title, globalMapping.TargetType);

                        if (globalMapping.TargetType == "local")
                        {
                            isLocal = true;
                            isManualMapping = true;
                            manualMappingType = "jellyfin";
                            manualMappingId = globalMapping.LocalId;
                        }
                        else if (globalMapping.TargetType == "external")
                        {
                            isLocal = false;
                            externalProvider = ResolvePreferredExternalProvider(globalMapping);
                            isManualMapping = true;
                            manualMappingType = "external";
                            manualMappingId = globalMapping.ExternalId;
                        }
                    }
                    else
                    {
                        // No manual mapping and not in cache - it's missing
                        // Fall back to ordered matched-tracks cache so auto local/external matches
                        // are shown correctly even when playlist item cache lacks Spotify ProviderIds.
                        if (PlaylistTrackStatusResolver.TryResolveFromMatchedTrack(
                                matchedTracksBySpotifyId,
                                track.SpotifyId,
                                out var resolvedIsLocal,
                                out var resolvedExternalProvider))
                        {
                            isLocal = resolvedIsLocal;
                            externalProvider = resolvedExternalProvider;
                            _logger.LogDebug(
                                "✓ Track {Title} ({SpotifyId}) resolved from matched cache as {Type}",
                                track.Title,
                                track.SpotifyId,
                                isLocal == true ? "local" : "external");
                        }
                        else
                        {
                            isLocal = null;
                            externalProvider = null;
                            _logger.LogDebug(
                                "✗ Track {Title} ({SpotifyId}) is MISSING (not in cache, no manual mapping, no matched cache)",
                                track.Title, track.SpotifyId);
                        }
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
                var cacheKey = $"lyrics:{track.PrimaryArtist}:{track.Title}:{track.Album}:{track.DurationMs / 1000}";
                var existingLyrics = await _cache.GetStringAsync(cacheKey);
                var hasLyrics = !string.IsNullOrEmpty(existingLyrics);
                if (isLocal.HasValue)
                {
                    matchedTrackCount++;
                }

                tracksWithStatus.Add(new
                {
                    position = track.Position,
                    title = track.Title,
                    artists = track.Artists,
                    album = track.Album,
                    isrc = track.Isrc,
                    spotifyId = track.SpotifyId,
                    durationMs = track.DurationMs,
                    albumArtUrl = track.AlbumArtUrl,
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
                sourceProvider = "spotify",
                targetBackend,
                matchedTracks = matchedTrackCount,
                unmatchedTracks = Math.Max(0, spotifyTracks.Count - matchedTrackCount),
                tracks = tracksWithStatus
            });
        }

        // Fallback: Cache not available, use matched tracks cache
        _logger.LogWarning("Playlist cache not available for {Playlist}, using fallback", decodedName);

        foreach (var track in spotifyTracks)
        {
            bool? isLocal = null;
            string? externalProvider = null;

            // Check for manual Jellyfin mapping
            var manualMappingKey = $"spotify:manual-map:{decodedName}:{track.SpotifyId}";
            var manualJellyfinId = await _cache.GetAsync<string>(manualMappingKey);

            if (!string.IsNullOrEmpty(manualJellyfinId))
            {
                isLocal = true;
            }
            else
            {
                // Check for external manual mapping
                var externalMappingKey = $"spotify:external-map:{decodedName}:{track.SpotifyId}";
                var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);

                if (!string.IsNullOrEmpty(externalMappingJson))
                {
                    try
                    {
                        using var extDoc = JsonDocument.Parse(externalMappingJson);
                        var extRoot = extDoc.RootElement;

                        string? provider = null;

                        if (extRoot.TryGetProperty("provider", out var providerEl))
                        {
                            provider = providerEl.GetString();
                        }

                        if (!string.IsNullOrEmpty(provider))
                        {
                            isLocal = false;
                            externalProvider = NormalizeExternalProviderForDisplay(provider);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process external manual mapping for {Title}", track.Title);
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

            if (isLocal == false)
            {
                externalProvider = NormalizeExternalProviderForDisplay(externalProvider);
                if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(externalProvider))
                {
                    isLocal = null;
                    externalProvider = null;
                }
            }

            tracksWithStatus.Add(new
            {
                position = track.Position,
                title = track.Title,
                artists = track.Artists,
                album = track.Album,
                isrc = track.Isrc,
                spotifyId = track.SpotifyId,
                durationMs = track.DurationMs,
                albumArtUrl = track.AlbumArtUrl,
                isLocal = isLocal,
                externalProvider = externalProvider,
                provider = isLocal == true ? targetBackend : externalProvider,
                matchState = isLocal == true ? "local" : isLocal == false ? "external" : "unmatched",
                searchQuery = isLocal != true ? $"{track.Title} {track.PrimaryArtist}" : null
            });
            if (isLocal.HasValue)
            {
                matchedTrackCount++;
            }
        }

        return Ok(new
        {
            name = decodedName,
            trackCount = spotifyTracks.Count,
            artworkUrl = playlistArtworkUrl,
            sourceProvider = "spotify",
            targetBackend,
            matchedTracks = matchedTrackCount,
            unmatchedTracks = Math.Max(0, spotifyTracks.Count - matchedTrackCount),
            tracks = tracksWithStatus
        });
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
        _helperService.InvalidatePlaylistSummaryCache();

        // Clear ALL playlist stats caches
        var configuredPlaylists = await GetConfiguredPlaylistsAsync();
        foreach (var playlist in configuredPlaylists)
        {
            var statsCacheKey = $"spotify:playlist:stats:{playlist.Name}";
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
            var statsCacheKey = $"spotify:playlist:stats:{decodedName}";
            await _cache.DeleteAsync(statsCacheKey);

            // Then invalidate playlist summary cache (will rebuild with fresh stats)
            _helperService.InvalidatePlaylistSummaryCache();

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
            var jellyfinSignatureCacheKey = $"spotify:playlist:jellyfin-signature:{decodedName}";
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
            _helperService.InvalidatePlaylistSummaryCache();

            // Clear playlist stats cache to force recalculation from new mappings
            var statsCacheKey = $"spotify:playlist:stats:{decodedName}";
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
            _helperService.InvalidatePlaylistSummaryCache();

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
        if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(normalizedProvider))
        {
            return BadRequest(new { error = $"{provider} is metadata-only and cannot be used as a playable track mapping" });
        }

        if (normalizedProvider != "deezer" && normalizedProvider != "qobuz" && normalizedProvider != "applemusic")
        {
            return BadRequest(new { error = "Unsupported provider" });
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
                .Where(s => string.IsNullOrWhiteSpace(s.ExternalProvider) ||
                            string.Equals(s.ExternalProvider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
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

            if (hasJellyfinMapping)
            {
                // Store Jellyfin mapping in cache (NO EXPIRATION - manual mappings are permanent)
                var mappingKey = $"spotify:manual-map:{decodedName}:{request.SpotifyId}";
                await _cache.SetAsync(mappingKey, request.JellyfinId!);

                // Also save to file for persistence across restarts
                await _helperService.SaveManualMappingToFileAsync(decodedName, request.SpotifyId, request.JellyfinId!, null, null);

                _logger.LogInformation("Manual Jellyfin mapping saved: {Playlist} - Spotify {SpotifyId} → Jellyfin {JellyfinId}",
                    decodedName, request.SpotifyId, request.JellyfinId);
            }
            else
            {
                // Store external mapping in cache (NO EXPIRATION - manual mappings are permanent)
                var externalMappingKey = $"spotify:external-map:{decodedName}:{request.SpotifyId}";
                normalizedProvider = request.ExternalProvider!.ToLowerInvariant(); // Normalize to lowercase
                if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(normalizedProvider))
                {
                    return BadRequest(new
                    {
                        error = $"{request.ExternalProvider} is metadata-only and cannot be used as a playable track mapping"
                    });
                }

                normalizedExternalId = NormalizeExternalTrackId(normalizedProvider, request.ExternalId!);
                var externalMapping = new { provider = normalizedProvider, id = normalizedExternalId };
                await _cache.SetAsync(externalMappingKey, externalMapping);

                // Also save to file for persistence across restarts
                await _helperService.SaveManualMappingToFileAsync(decodedName, request.SpotifyId, null, normalizedProvider, normalizedExternalId);

                _logger.LogInformation("Manual external mapping saved: {Playlist} - Spotify {SpotifyId} → {Provider} {ExternalId}",
                    decodedName, request.SpotifyId, normalizedProvider, normalizedExternalId);
            }

            // Keep global Spotify mappings in sync so the dedicated mappings page reflects manual map actions.
            var existingGlobalMapping = await _mappingService.GetMappingAsync(request.SpotifyId);
            var globalMetadata = existingGlobalMapping?.Metadata;

            var globalMappingSaved = hasJellyfinMapping
                ? await _mappingService.SaveManualMappingAsync(
                    request.SpotifyId,
                    "local",
                    localId: request.JellyfinId!,
                    metadata: globalMetadata)
                : await _mappingService.SaveManualMappingAsync(
                    request.SpotifyId,
                    "external",
                    externalProvider: normalizedProvider!,
                    externalId: normalizedExternalId!,
                    metadata: globalMetadata);

            if (globalMappingSaved)
            {
                _logger.LogInformation("Global mapping synchronized for Spotify {SpotifyId}", request.SpotifyId);
            }
            else
            {
                _logger.LogWarning("Global mapping synchronization skipped for Spotify {SpotifyId}", request.SpotifyId);
            }

            // Clear all related caches to force rebuild
            var matchedCacheKey = $"spotify:matched:{decodedName}";
            var orderedCacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(decodedName);
            var playlistItemsKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(decodedName);
            var statsCacheKey = $"spotify:playlist:stats:{decodedName}";

            await _cache.DeleteAsync(matchedCacheKey);
            await _cache.DeleteAsync(orderedCacheKey);
            await _cache.DeleteAsync(playlistItemsKey);
            await _cache.DeleteAsync(statsCacheKey);

            // Also delete file caches to force rebuild
            try
            {
                var cacheDir = "/app/cache/spotify";
                var safeName = AdminHelperService.SanitizeFileName(decodedName);
                var matchedFile = Path.Combine(cacheDir, $"{safeName}_matched.json");
                var itemsFile = Path.Combine(cacheDir, $"{safeName}_items.json");
                var statsFile = Path.Combine(cacheDir, $"{safeName}_stats.json");

                if (System.IO.File.Exists(matchedFile))
                {
                    System.IO.File.Delete(matchedFile);
                    _logger.LogInformation("Deleted matched tracks file cache for {Playlist}", decodedName);
                }

                if (System.IO.File.Exists(itemsFile))
                {
                    System.IO.File.Delete(itemsFile);
                    _logger.LogDebug("Deleted playlist items file cache for {Playlist}", decodedName);
                }

                if (System.IO.File.Exists(statsFile))
                {
                    System.IO.File.Delete(statsFile);
                    _logger.LogDebug("Deleted stats file cache for {Playlist}", decodedName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file caches for {Playlist}", decodedName);
            }

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
    public async Task<IActionResult> MatchAllPlaylistTracks()
    {
        _logger.LogInformation("Manual track matching triggered for all playlists");

        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }

        try
        {
            await _matchingService.TriggerMatchingAsync();
            return Ok(new { message = "Track matching triggered for all playlists", timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger track matching for all playlists");
            return StatusCode(500, new { error = "Failed to trigger track matching" });
        }
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
        _helperService.InvalidatePlaylistSummaryCache();
        return Ok(new { message = "Playlist configuration updated.", changeVersion = result.ChangeVersion });
    }


    /// <summary>
    /// Save lyrics mapping to file for persistence across restarts.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
}
