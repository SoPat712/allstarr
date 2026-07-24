using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Filters;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class JellyfinAdminController : ControllerBase
{
    private readonly ILogger<JellyfinAdminController> _logger;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly IApplicationCache _cache;
    private readonly IConfiguration _configuration;
    private readonly SpotifyImportSettings _spotifyImportSettings;

    public JellyfinAdminController(
        ILogger<JellyfinAdminController> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        AdminHelperService helperService,
        IApplicationCache cache,
        IConfiguration configuration,
        IOptions<SpotifyImportSettings> spotifyImportSettings)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _helperService = helperService;
        _cache = cache;
        _configuration = configuration;
        _spotifyImportSettings = spotifyImportSettings.Value;
    }

    private bool TryGetCurrentSession(out AdminAuthSession session)
    {
        session = null!;
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) &&
            sessionObj is AdminAuthSession typedSession)
        {
            session = typedSession;
            return true;
        }

        return false;
    }

    private static bool UserIdsEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static SpotifyPlaylistConfig? ResolveScopedLinkedPlaylist(
        IReadOnlyCollection<SpotifyPlaylistConfig> allLinkedForPlaylist,
        bool isAdministrator,
        string? requestedUserId,
        string? sessionUserId)
    {
        if (isAdministrator && string.IsNullOrWhiteSpace(requestedUserId))
        {
            return allLinkedForPlaylist.FirstOrDefault();
        }

        var ownerUserId = requestedUserId ?? sessionUserId;

        // Prefer user-scoped entries, but treat legacy/global entries (without UserId)
        // as linked for all scopes so old configurations render correctly.
        return allLinkedForPlaylist.FirstOrDefault(p => UserIdsEqual(p.UserId, ownerUserId))
               ?? allLinkedForPlaylist.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.UserId));
    }

    private static bool IsValidCronExpression(string cron)
    {
        var cronParts = cron.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return cronParts.Length == 5;
    }

    private HttpRequestMessage CreateJellyfinRequestForSession(HttpMethod method, string url, AdminAuthSession session)
    {
        if (session.IsAdministrator)
        {
            return _helperService.CreateJellyfinRequest(method, url);
        }

        var request = new HttpRequestMessage(method, url);
        var authHeader =
            $"MediaBrowser Client=\"AllstarrAdmin\", Device=\"WebUI\", DeviceId=\"allstarr-admin-webui\", Version=\"{AppVersion.Version}\", Token=\"{session.JellyfinAccessToken}\"";
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", authHeader);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", session.JellyfinAccessToken);
        return request;
    }

    private async Task<(string? Name, IActionResult? Error)> TryGetJellyfinPlaylistNameAsync(
        string jellyfinPlaylistId,
        string userId,
        AdminAuthSession session)
    {
        var playlistUrl = $"{_jellyfinSettings.Url}/Items/{jellyfinPlaylistId}?UserId={Uri.EscapeDataString(userId)}";
        var playlistRequest = CreateJellyfinRequestForSession(HttpMethod.Get, playlistUrl, session);
        var playlistResponse = await _jellyfinHttpClient.SendAsync(playlistRequest);

        if (playlistResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (null, NotFound(new { error = "Jellyfin playlist not found for this user" }));
        }

        if (playlistResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                new { error = "User does not have access to this Jellyfin playlist" }));
        }

        if (!playlistResponse.IsSuccessStatusCode)
        {
            var errorBody = await playlistResponse.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to resolve Jellyfin playlist {PlaylistId} for user {UserId}: {StatusCode} - {Body}",
                jellyfinPlaylistId, userId, playlistResponse.StatusCode, errorBody);
            return (null, StatusCode((int)playlistResponse.StatusCode,
                new { error = "Failed to fetch Jellyfin playlist details" }));
        }

        using var playlistDoc = await JsonDocument.ParseAsync(await playlistResponse.Content.ReadAsStreamAsync());
        var root = playlistDoc.RootElement;
        var itemType = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() : null;
        if (!string.Equals(itemType, "Playlist", StringComparison.OrdinalIgnoreCase))
        {
            return (null, BadRequest(new { error = "Selected Jellyfin item is not a playlist" }));
        }

        var playlistName = root.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return (null, BadRequest(new { error = "Jellyfin playlist name is missing" }));
        }

        return (playlistName.Trim(), null);
    }

    [HttpGet("jellyfin/users")]
    public async Task<IActionResult> GetJellyfinUsers()
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }

        try
        {
            var url = $"{_jellyfinSettings.Url}/Users";

            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);

            var response = await _jellyfinHttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin users: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch users from Jellyfin" });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var users = new List<object>();

            foreach (var user in doc.RootElement.EnumerateArray())
            {
                var id = user.GetProperty("Id").GetString();
                var name = user.GetProperty("Name").GetString();

                users.Add(new { id, name });
            }

            return Ok(new { users });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin users");
            return StatusCode(500, new { error = "Failed to fetch users" });
        }
    }

    /// <summary>
    /// Get all Jellyfin libraries (virtual folders)
    /// </summary>
    [HttpGet("jellyfin/libraries")]
    public async Task<IActionResult> GetJellyfinLibraries()
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }

        try
        {
            var url = $"{_jellyfinSettings.Url}/Library/VirtualFolders";

            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);

            var response = await _jellyfinHttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin libraries: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch libraries from Jellyfin" });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var libraries = new List<object>();

            foreach (var lib in doc.RootElement.EnumerateArray())
            {
                var name = lib.GetProperty("Name").GetString();
                var itemId = lib.TryGetProperty("ItemId", out var id) ? id.GetString() : null;
                var collectionType = lib.TryGetProperty("CollectionType", out var ct) ? ct.GetString() : null;

                libraries.Add(new { id = itemId, name, collectionType });
            }

            return Ok(new { libraries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin libraries");
            return StatusCode(500, new { error = "Failed to fetch libraries" });
        }
    }

    /// <summary>
    /// Get all playlists from the user's Spotify account
    /// </summary>
    [HttpGet("jellyfin/playlists")]
    public async Task<IActionResult> GetJellyfinPlaylists(
        [FromQuery] string? userId = null,
        [FromQuery] bool includeStats = true)
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }

        if (!TryGetCurrentSession(out var session))
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        var requestedUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        if (!session.IsAdministrator)
        {
            if (!string.IsNullOrWhiteSpace(requestedUserId) && !UserIdsEqual(requestedUserId, session.UserId))
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "You can only view your own Jellyfin playlists" });
            }

            requestedUserId = session.UserId;
        }

        try
        {
            var url = $"{_jellyfinSettings.Url}/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=ProviderIds,ChildCount,RecursiveItemCount,SongCount";
            if (!string.IsNullOrWhiteSpace(requestedUserId))
            {
                url += $"&UserId={Uri.EscapeDataString(requestedUserId)}";
            }

            var request = CreateJellyfinRequestForSession(HttpMethod.Get, url, session);
            var response = await _jellyfinHttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin playlists: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch playlists from Jellyfin" });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var playlists = new List<object>();
            var configuredPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();

            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("Id").GetString();
                    var name = item.GetProperty("Name").GetString();

                    var childCount = 0;
                    if (item.TryGetProperty("ChildCount", out var cc) && cc.ValueKind == JsonValueKind.Number)
                    {
                        childCount = cc.GetInt32();
                    }
                    else if (item.TryGetProperty("SongCount", out var sc) && sc.ValueKind == JsonValueKind.Number)
                    {
                        childCount = sc.GetInt32();
                    }
                    else if (item.TryGetProperty("RecursiveItemCount", out var ric) && ric.ValueKind == JsonValueKind.Number)
                    {
                        childCount = ric.GetInt32();
                    }

                    var allLinkedForPlaylist = configuredPlaylists
                        .Where(p => p.JellyfinId.Equals(id, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var scopedLinkedPlaylist = ResolveScopedLinkedPlaylist(
                        allLinkedForPlaylist,
                        session.IsAdministrator,
                        requestedUserId,
                        session.UserId);

                    var isConfigured = scopedLinkedPlaylist != null;
                    var isLinkedByAnotherUser = !isConfigured && allLinkedForPlaylist.Count > 0;
                    var linkedSpotifyId = scopedLinkedPlaylist?.Id;

                    var statsUserId = requestedUserId;
                    var trackStats = (LocalTracks: 0, ExternalTracks: 0, ExternalAvailable: 0);
                    if (isConfigured && includeStats)
                    {
                        trackStats = await GetPlaylistTrackStats(id!, session, statsUserId);
                    }

                    var actualTrackCount = isConfigured
                        ? (includeStats ? trackStats.LocalTracks + trackStats.ExternalTracks : childCount)
                        : childCount;

                    playlists.Add(new
                    {
                        id,
                        name,
                        trackCount = actualTrackCount,
                        linkedSpotifyId,
                        isConfigured,
                        isLinkedByAnotherUser,
                        linkedOwnerUserId = scopedLinkedPlaylist?.UserId ??
                                            allLinkedForPlaylist.FirstOrDefault()?.UserId,
                        statsPending = isConfigured && !includeStats,
                        localTracks = trackStats.LocalTracks,
                        externalTracks = trackStats.ExternalTracks,
                        externalAvailable = trackStats.ExternalAvailable
                    });
                }
            }

            return Ok(new { playlists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin playlists");
            return StatusCode(500, new { error = "Failed to fetch playlists" });
        }
    }

    /// <summary>
    /// Get track statistics for a playlist (local vs external)
    /// </summary>
    private async Task<(int LocalTracks, int ExternalTracks, int ExternalAvailable)> GetPlaylistTrackStats(
        string playlistId,
        AdminAuthSession session,
        string? requestedUserId = null)
    {
        try
        {
            // Jellyfin requires a UserId to fetch playlist items
            // Non-admin users are always scoped to their own Jellyfin user.
            var userId = string.IsNullOrWhiteSpace(requestedUserId)
                ? (session.IsAdministrator ? _jellyfinSettings.UserId : session.UserId)
                : requestedUserId.Trim();

            // Admin fallback: if no configured user, try to get the first Jellyfin user.
            if (session.IsAdministrator && string.IsNullOrEmpty(userId))
            {
                var usersRequest = CreateJellyfinRequestForSession(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users", session);
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
                _logger.LogWarning("No user ID available to fetch playlist items for {PlaylistId}", playlistId);
                return (0, 0, 0);
            }

            var url = $"{_jellyfinSettings.Url}/Playlists/{playlistId}/Items?UserId={userId}&Fields=Path";
            var request = CreateJellyfinRequestForSession(HttpMethod.Get, url, session);

            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch playlist items for {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
                return (0, 0, 0);
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var localTracks = 0;
            var externalTracks = 0;
            var externalAvailable = 0;

            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    // Simpler detection: Check if Path exists and is not empty
                    // External tracks from allstarr won't have a Path property
                    var hasPath = item.TryGetProperty("Path", out var pathProp) &&
                                  pathProp.ValueKind == JsonValueKind.String &&
                                  !string.IsNullOrEmpty(pathProp.GetString());

                    if (hasPath)
                    {
                        var pathStr = pathProp.GetString()!;
                        // Check if it's a real file path (not a URL)
                        if (pathStr.StartsWith("/") || pathStr.Contains(":\\"))
                        {
                            localTracks++;
                        }
                        else
                        {
                            // It's a URL or external source
                            externalTracks++;
                            externalAvailable++;
                        }
                    }
                    else
                    {
                        // No path means it's external
                        externalTracks++;
                        externalAvailable++;
                    }
                }

                _logger.LogDebug("Playlist {PlaylistId} stats: {Local} local, {External} external",
                    playlistId, localTracks, externalTracks);
            }
            else
            {
                _logger.LogWarning("No Items property in playlist response for {PlaylistId}", playlistId);
            }

            return (localTracks, externalTracks, externalAvailable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track stats for playlist {PlaylistId}", playlistId);
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Link a Jellyfin playlist to a Spotify playlist
    /// </summary>
    [HttpPost("jellyfin/playlists/{jellyfinPlaylistId}/link")]
    public async Task<IActionResult> LinkPlaylist(string jellyfinPlaylistId, [FromBody] LinkPlaylistRequest request)
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }

        if (!TryGetCurrentSession(out var session))
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        if (string.IsNullOrWhiteSpace(request.SpotifyPlaylistId))
        {
            return BadRequest(new { error = "SpotifyPlaylistId is required" });
        }

        var syncSchedule = string.IsNullOrWhiteSpace(request.SyncSchedule)
            ? "0 8 * * *"
            : request.SyncSchedule.Trim();
        if (!IsValidCronExpression(syncSchedule))
        {
            return BadRequest(new { error = "Invalid cron format. Expected: minute hour day month dayofweek" });
        }

        var ownerUserId = string.IsNullOrWhiteSpace(request.UserId) ? session.UserId : request.UserId.Trim();
        if (!session.IsAdministrator && !UserIdsEqual(ownerUserId, session.UserId))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "You can only link playlists for your own Jellyfin user" });
        }

        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return BadRequest(new { error = "Unable to determine Jellyfin owner user" });
        }

        var (playlistName, playlistError) = await TryGetJellyfinPlaylistNameAsync(jellyfinPlaylistId, ownerUserId, session);
        if (playlistError != null)
        {
            return playlistError;
        }

        _logger.LogInformation(
            "Linking Jellyfin playlist {JellyfinId} ({PlaylistName}) to Spotify playlist {SpotifyId} for user {OwnerUserId}",
            jellyfinPlaylistId, playlistName, request.SpotifyPlaylistId, ownerUserId);

        var currentPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();

        var existingByJellyfinId = currentPlaylists
            .FirstOrDefault(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
        if (existingByJellyfinId != null)
        {
            if (UserIdsEqual(existingByJellyfinId.UserId, ownerUserId))
            {
                return BadRequest(new { error = "This Jellyfin playlist is already linked for this user" });
            }

            return BadRequest(new { error = "This Jellyfin playlist is already linked by another user" });
        }

        var existingByName = currentPlaylists
            .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
        if (existingByName != null)
        {
            return BadRequest(new { error = $"Playlist name '{playlistName}' is already configured" });
        }

        currentPlaylists.Add(new SpotifyPlaylistConfig
        {
            Name = playlistName!,
            Id = request.SpotifyPlaylistId.Trim(),
            JellyfinId = jellyfinPlaylistId,
            LocalTracksPosition = LocalTracksPosition.First,
            SyncSchedule = syncSchedule,
            UserId = ownerUserId
        });

        var playlistsJson = AdminHelperService.SerializePlaylistsForEnv(currentPlaylists);
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };

        return await _helperService.UpdateEnvConfigAsync(updateRequest.Updates);
    }

    /// <summary>
    /// Unlink a playlist (remove from configuration)
    /// </summary>
    [HttpDelete("jellyfin/playlists/{jellyfinPlaylistId}/unlink")]
    public async Task<IActionResult> UnlinkPlaylist(string jellyfinPlaylistId)
    {
        if (!TryGetCurrentSession(out var session))
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        var decodedIdentifier = Uri.UnescapeDataString(jellyfinPlaylistId);
        var currentPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();
        var playlist = currentPlaylists.FirstOrDefault(p =>
            p.JellyfinId.Equals(decodedIdentifier, StringComparison.OrdinalIgnoreCase));

        // Backward compatibility: older UI versions unlink by playlist name.
        if (playlist == null)
        {
            playlist = currentPlaylists.FirstOrDefault(p =>
                p.Name.Equals(decodedIdentifier, StringComparison.OrdinalIgnoreCase));
        }

        if (playlist == null)
        {
            return NotFound(new { error = "Playlist link not found" });
        }

        if (!session.IsAdministrator && !UserIdsEqual(playlist.UserId, session.UserId))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "You can only unlink playlists you own" });
        }

        currentPlaylists.Remove(playlist);
        var playlistsJson = AdminHelperService.SerializePlaylistsForEnv(currentPlaylists);
        var updates = new Dictionary<string, string>
        {
            ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
        };

        return await _helperService.UpdateEnvConfigAsync(updates);
    }

}
