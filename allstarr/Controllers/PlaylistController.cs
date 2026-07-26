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
using allstarr.Core.Playlists;
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
    private readonly ITrackMatchRepository _trackMatchCommands;
    private readonly IApplicationCache _cache;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
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
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _playlistFetcher = playlistFetcher;
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
            var durable = await ReadDurablePlaylistAsync(config.Name);
            var playlistInfo = new Dictionary<string, object?>
            {
                ["name"] = config.Name,
                ["id"] = config.Id,
                ["jellyfinId"] = config.JellyfinId,
                ["localTracksPosition"] = config.LocalTracksPosition.ToString(),
                ["syncSchedule"] = config.SyncSchedule ?? "0 8 * * *",
                ["trackCount"] = durable?.Entries.Count ?? 0,
                ["localTracks"] = durable?.LocalCount ?? 0,
                ["externalTracks"] = 0,
                ["lastFetched"] = durable?.RetrievedAt,
                ["lastSuccessfulSyncAt"] = durable?.CompletedAt,
                ["cacheAge"] = null as string,
                ["artworkUrl"] = DurableArtworkUrl(durable),
                ["providerBreakdown"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                ["sourceProvider"] = durable?.SourceProviderId ?? "spotify",
                ["durationMs"] = durable?.DurationMilliseconds
            };

            try
            {
                playlistInfo["artworkSource"] = durable?.ArtworkReferenceKey != null
                    ? "playlist"
                    : "target";
                if (durable?.RetrievedAt is { } fetchedAt)
                {
                    var age = DateTimeOffset.UtcNow - fetchedAt;
                    playlistInfo["cacheAge"] = age.TotalHours < 1
                        ? $"{age.TotalMinutes:F0}m"
                        : $"{age.TotalHours:F1}h";
                }

                var providerBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (durable?.LocalCount > 0) providerBreakdown[targetBackend] = durable.LocalCount;
                var coverage = new PlaylistCoverage(
                    durable?.LocalCount ?? 0,
                    0,
                    durable?.MissingCount ?? 0,
                    providerBreakdown);
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

    private Task<DurablePlaylistProjection?> ReadDurablePlaylistAsync(string playlistName)
    {
        if (!HttpContext.Items.TryGetValue(
                AdminAuthSessionService.HttpContextSessionItemKey,
                out var value) ||
            value is not AdminAuthSession session ||
            !session.TenantId.HasValue)
        {
            return Task.FromResult<DurablePlaylistProjection?>(null);
        }

        return HttpContext.RequestServices
            .GetRequiredService<DurablePlaylistProjectionReader>()
            .ReadByNameAsync(
                session.TenantId.Value,
                session.IsAdministrator ? null : session.AllstarrUserId,
                playlistName,
                HttpContext.RequestAborted);
    }

    private static string? DurableArtworkUrl(DurablePlaylistProjection? playlist) =>
        playlist?.ArtworkReferenceKey == null
            ? null
            : $"/api/admin/playlist-sources/{playlist.ProviderAccountId}/playlists/" +
              $"{Uri.EscapeDataString(playlist.SourcePlaylistId)}/artwork";


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
    /// Gets the latest durable playlist generation with its current match state.
    /// </summary>
    [HttpGet("playlists/{name}/tracks")]
    public async Task<IActionResult> GetPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        var playlist = await ReadDurablePlaylistAsync(decodedName);
        var configured = (await GetConfiguredPlaylistsAsync()).FirstOrDefault(item =>
            item.Name.Equals(decodedName, StringComparison.OrdinalIgnoreCase));
        var syncSchedule = configured?.SyncSchedule ?? "0 8 * * *";
        if (playlist == null)
        {
            return Ok(new
            {
                name = decodedName,
                trackCount = 0,
                sourceProvider = "spotify",
                totalPlayable = 0,
                localTracks = 0,
                externalTracks = 0,
                matchedTracks = 0,
                unmatchedTracks = 0,
                durationMs = (long?)null,
                syncSchedule,
                lastSourceRefreshAt = (DateTimeOffset?)null,
                lastSuccessfulSyncAt = (DateTimeOffset?)null,
                nextSyncAt = GetNextScheduledOccurrence(syncSchedule),
                matchStatus = "pending",
                tracks = Array.Empty<object>()
            });
        }

        var tracks = playlist.Entries.Select((track, index) =>
        {
            var local = track.BackendItemId != null;
            var artwork = local && playlist.TargetProtocol == "jellyfin"
                ? $"/Items/{Uri.EscapeDataString(track.BackendItemId!)}/Images/Primary"
                : null;
            return (object)new
            {
                position = index + 1,
                sourcePosition = track.Position,
                externalSnapshotId = track.ExternalSnapshotId,
                title = track.Title,
                artists = track.Artists,
                album = track.Album,
                isrc = track.Isrc,
                spotifyId = track.ExternalId,
                durationMs = track.DurationMilliseconds,
                albumArtUrl = artwork,
                backendItemId = track.BackendItemId,
                isLocal = local ? true : (bool?)null,
                externalProvider = (string?)null,
                provider = local ? playlist.TargetProtocol : null,
                matchState = local ? "local" : "unmatched",
                decisionState = track.MatchState?.ToString().ToLowerInvariant(),
                searchQuery = local ? null : $"{track.Title} {track.Artists.FirstOrDefault()}"
            };
        }).ToArray();
        var matched = playlist.LocalCount;
        return Ok(new
        {
            name = playlist.Name,
            trackCount = playlist.Entries.Count,
            artworkUrl = DurableArtworkUrl(playlist),
            artworkSource = playlist.ArtworkReferenceKey == null ? "target" : "playlist",
            sourceProvider = playlist.SourceProviderId,
            targetBackend = playlist.TargetProtocol,
            totalPlayable = matched,
            localTracks = matched,
            externalTracks = 0,
            matchedTracks = matched,
            unmatchedTracks = playlist.MissingCount,
            durationMs = playlist.DurationMilliseconds,
            syncSchedule,
            lastSourceRefreshAt = playlist.RetrievedAt,
            lastSuccessfulSyncAt = playlist.CompletedAt,
            nextSyncAt = GetNextScheduledOccurrence(syncSchedule),
            syncState = playlist.SyncState?.ToString().ToLowerInvariant(),
            matchStatus = matched == playlist.Entries.Count
                ? "ready"
                : matched == 0
                    ? "rematch_required"
                    : "partial",
            tracks
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

            // Invalidate the short-lived derived summary.
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

            if (hasJellyfinMapping)
            {
                return Ok(new
                {
                    message = "Mapping saved",
                    track = new
                    {
                        id = request.JellyfinId,
                        isLocal = true
                    }
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
                message = "Mapping saved",
                track = mappedTrack
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual mapping");
            return StatusCode(500, new { error = "Failed to save mapping" });
        }
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

        var playlistsJson = SpotifyPlaylistConfigParser.Serialize(currentPlaylists);

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

        var playlistsJson = SpotifyPlaylistConfigParser.Serialize(currentPlaylists);

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
        var playlistsJson = SpotifyPlaylistConfigParser.Serialize(currentPlaylists);
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
        return current.Value is string json && !string.IsNullOrWhiteSpace(json)
            ? SpotifyPlaylistConfigParser.Parse(json)
            : _spotifyImportSettings.Playlists.ToList();
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
