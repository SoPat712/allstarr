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
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly IServiceProvider _serviceProvider;
    private const int PlaylistSummarySchemaVersion = 10;

    public PlaylistController(
        ILogger<PlaylistController> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IHttpClientFactory httpClientFactory,
        AdminHelperService helperService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _helperService = helperService;
        _serviceProvider = serviceProvider;
    }

    [HttpGet("playlists")]
    public async Task<IActionResult> GetPlaylists()
    {
        // Version 3 owns playlist configuration in the tenant's durable settings.
        // Reading the store directly also avoids waiting for the in-memory projector.
        var configuredPlaylists = await GetConfiguredPlaylistsAsync();

        var playlists = new List<object>();

        foreach (var config in configuredPlaylists)
        {
            var durable = await ReadDurablePlaylistAsync(config.Name);
            var total = durable?.TotalCount ?? 0;
            var local = durable?.LocalCount ?? 0;
            var external = durable?.ExternalCount ?? 0;
            var missing = durable?.MissingCount ?? 0;
            var playable = durable?.PlayableCount ?? 0;
            var matched = durable?.MatchedCount ?? 0;
            var playlistInfo = new Dictionary<string, object?>
            {
                ["name"] = config.Name,
                ["id"] = config.Id,
                ["jellyfinId"] = config.JellyfinId,
                ["localTracksPosition"] = config.LocalTracksPosition.ToString(),
                ["syncSchedule"] = config.SyncSchedule ?? "0 8 * * *",
                ["trackCount"] = total,
                ["localTracks"] = local,
                ["externalTracks"] = external,
                ["externalMatched"] = external,
                ["externalMissing"] = missing,
                ["externalTotal"] = external + missing,
                ["unmatchedTracks"] = missing,
                ["totalInJellyfin"] = playable,
                ["totalPlayable"] = playable,
                ["unknownDurationTracks"] = durable?.UnknownDurationCount ?? 0,
                ["lastFetched"] = durable?.RetrievedAt,
                ["lastSuccessfulSyncAt"] = durable?.CompletedAt,
                ["cacheAge"] = null as string,
                ["artworkUrl"] = DurableArtworkUrl(durable),
                ["providerBreakdown"] = durable?.RouteCounts
                    .Where(item => !item.Key.Equals("unresolved", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                ["sourceProvider"] = durable?.SourceProviderId ?? "spotify",
                ["durationMs"] = durable?.DurationMilliseconds,
                ["materializationVerification"] = MaterializationVerification(durable)
            };

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

            EnrichPlaylistSummary(playlistInfo, config.SyncSchedule, total, matched);
            playlists.Add(playlistInfo);
        }

        var inventory = await GetPlaylistInventoryAsync(configuredPlaylists);

        return Ok(new { schemaVersion = PlaylistSummarySchemaVersion, playlists, inventory });
    }

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

    private static string TrackArtworkUrl(string itemId) =>
        $"/api/admin/downloads/artwork/{Uri.EscapeDataString(itemId)}";

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

    private static void EnrichPlaylistSummary(
        Dictionary<string, object?> playlistInfo,
        string? syncSchedule,
        int trackCount,
        int playableCount)
    {
        var matchedTracks = Math.Clamp(playableCount, 0, trackCount);
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
                unknownDurationTracks = 0,
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
            var local = track.RouteKind == "local";
            var artwork = local
                ? TrackArtworkUrl(track.BackendItemId!)
                : track.RouteKind == "external" &&
                  !string.IsNullOrWhiteSpace(track.RouteProviderId)
                    ? TrackArtworkUrl($"ext-{track.RouteProviderId}-song-{track.ExternalId}")
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
                durationProvenance = track.DurationProvenance,
                durationRetrievedAt = track.DurationRetrievedAt,
                albumArtUrl = artwork,
                backendItemId = track.BackendItemId,
                isLocal = local ? true : (bool?)null,
                externalProvider = track.RouteKind == "external" ? track.RouteProviderId : null,
                provider = track.RouteProviderId,
                providerRoutes = track.ProviderRoutes.Select(route => new
                {
                    provider = route.ProviderId,
                    externalId = route.ExternalId,
                    pinned = route.IsManual
                }),
                matchState = track.RouteKind,
                decisionState = track.MatchState?.ToString().ToLowerInvariant(),
                searchQuery = local ? null : $"{track.Title} {track.Artists.FirstOrDefault()}"
            };
        }).ToArray();
        var matched = playlist.MatchedCount;
        return Ok(new
        {
            name = playlist.Name,
            trackCount = playlist.Entries.Count,
            artworkUrl = DurableArtworkUrl(playlist),
            artworkSource = playlist.ArtworkReferenceKey == null ? "target" : "playlist",
            sourceProvider = playlist.SourceProviderId,
            targetBackend = playlist.TargetProtocol,
            totalPlayable = playlist.PlayableCount,
            localTracks = playlist.LocalCount,
            externalTracks = playlist.ExternalCount,
            matchedTracks = matched,
            unmatchedTracks = playlist.MissingCount,
            reviewTracks = playlist.ReviewCount,
            rejectedTracks = playlist.RejectedCount,
            durationMs = playlist.DurationMilliseconds,
            unknownDurationTracks = playlist.UnknownDurationCount,
            syncSchedule,
            lastSourceRefreshAt = playlist.RetrievedAt,
            lastSuccessfulSyncAt = playlist.CompletedAt,
            nextSyncAt = GetNextScheduledOccurrence(syncSchedule),
            syncState = playlist.SyncState?.ToString().ToLowerInvariant(),
            materializationVerification = MaterializationVerification(playlist),
            matchStatus = matched == playlist.Entries.Count
                ? "ready"
                : matched == 0
                    ? "rematch_required"
                    : "partial",
            tracks
        });
    }

    private static object? MaterializationVerification(DurablePlaylistProjection? playlist) =>
        playlist?.VerificationCode == null ? null : new
        {
            code = playlist.VerificationCode,
            plannedTrackCount = playlist.PlannedTargetTrackCount,
            plannedDurationMs = playlist.PlannedTargetDurationMilliseconds,
            reportedTrackCount = playlist.VerifiedTargetTrackCount,
            reportedDurationMs = playlist.VerifiedTargetDurationMilliseconds,
            verifiedAt = playlist.VerifiedAt
        };

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
        return Ok(new { message = "Playlist configuration updated.", changeVersion = result.ChangeVersion });
    }


    /// <summary>
    /// Save lyrics mapping to file for persistence across restarts.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
}
