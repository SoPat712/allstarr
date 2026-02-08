using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using Microsoft.Extensions.Options;
using OtpNet;

namespace allstarr.Services.Spotify;

/// <summary>
/// Client for accessing Spotify's APIs directly.
/// 
/// Supports two modes:
/// 1. Official API - For public playlists and standard operations
/// 2. Web API (with session cookie) - For editorial/personalized playlists like Release Radar, Discover Weekly
/// 
/// The session cookie (sp_dc) is required because Spotify's official API doesn't expose
/// algorithmically generated "Made For You" playlists.
/// 
/// Uses TOTP-based authentication similar to the Jellyfin Spotify Import plugin.
/// </summary>
public class SpotifyApiClient : IDisposable
{
    private readonly ILogger<SpotifyApiClient> _logger;
    private readonly SpotifyApiSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _webApiClient;
    private readonly CookieContainer _cookieContainer;
    
    // Spotify API endpoints
    private const string OfficialApiBase = "https://api.spotify.com/v1";
    private const string WebApiBase = "https://api-partner.spotify.com/pathfinder/v1";
    private const string SpotifyBaseUrl = "https://open.spotify.com";
    private const string TokenEndpoint = "https://open.spotify.com/api/token";
    
    // URL for pre-scraped TOTP secrets (same as Jellyfin plugin uses)
    private const string TotpSecretsUrl = "https://raw.githubusercontent.com/xyloflake/spot-secrets-go/refs/heads/main/secrets/secretBytes.json";
    
    // Web API access token (obtained via session cookie)
    private string? _webAccessToken;
    private DateTime _webTokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    
    // Cached TOTP secrets
    private TotpSecret? _cachedTotpSecret;
    private DateTime _totpSecretFetchedAt = DateTime.MinValue;
    
    public SpotifyApiClient(
        ILogger<SpotifyApiClient> logger,
        IOptions<SpotifyApiSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        
        // Client for official API
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(OfficialApiBase),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        // Client for web API (requires session cookie)
        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookieContainer
        };
        
        if (!string.IsNullOrEmpty(_settings.SessionCookie))
        {
            _cookieContainer.SetCookies(
                new Uri(SpotifyBaseUrl),
                $"sp_dc={_settings.SessionCookie}");
        }
        
        _webApiClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        // Common headers for web API
        _webApiClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        _webApiClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _webApiClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
        _webApiClient.DefaultRequestHeaders.Add("app-platform", "WebPlayer");
        _webApiClient.DefaultRequestHeaders.Add("spotify-app-version", "1.2.46.25.g7f189073");
    }
    
    /// <summary>
    /// Gets an access token using the session cookie and TOTP authentication.
    /// This token can be used for both the official API and web API.
    /// </summary>
    public async Task<string?> GetWebAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.SessionCookie))
        {
            _logger.LogWarning("No Spotify session cookie configured");
            return null;
        }
        
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Return cached token if still valid
            if (!string.IsNullOrEmpty(_webAccessToken) && DateTime.UtcNow < _webTokenExpiry)
            {
                return _webAccessToken;
            }
            
            _logger.LogInformation("Fetching new Spotify web access token using TOTP authentication");
            
            // Fetch TOTP secrets if needed
            var totpSecret = await GetTotpSecretAsync(cancellationToken);
            if (totpSecret == null)
            {
                _logger.LogError("Failed to get TOTP secrets");
                return null;
            }
            
            // Generate TOTP
            var totpResult = await GenerateTotpAsync(totpSecret, cancellationToken);
            if (totpResult == null)
            {
                _logger.LogError("Failed to generate TOTP");
                return null;
            }
            
            var (otp, serverTime) = totpResult.Value;
            var clientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Build token URL with TOTP parameters
            var tokenUrl = $"{TokenEndpoint}?reason=init&productType=web-player&totp={otp}&totpServer={otp}&totpVer={totpSecret.Version}&sTime={serverTime}&cTime={clientTime}";
            
            _logger.LogDebug("Requesting token from: {Url}", tokenUrl.Replace(otp, "***"));
            
            var response = await _webApiClient.GetAsync(tokenUrl, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get Spotify access token: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(json);
            
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("No access token in Spotify response: {Json}", json);
                return null;
            }
            
            if (tokenResponse.IsAnonymous)
            {
                _logger.LogWarning("Spotify returned anonymous token - session cookie may be invalid");
            }
            
            _webAccessToken = tokenResponse.AccessToken;
            
            // Token typically expires in 1 hour, but we'll refresh early
            if (tokenResponse.ExpirationTimestampMs > 0)
            {
                _webTokenExpiry = DateTimeOffset.FromUnixTimeMilliseconds(tokenResponse.ExpirationTimestampMs).UtcDateTime;
                // Refresh 5 minutes early
                _webTokenExpiry = _webTokenExpiry.AddMinutes(-5);
            }
            else
            {
                _webTokenExpiry = DateTime.UtcNow.AddMinutes(55);
            }
            
            _logger.LogInformation("Obtained Spotify web access token, expires at {Expiry}, anonymous: {IsAnonymous}", 
                _webTokenExpiry, tokenResponse.IsAnonymous);
            return _webAccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Spotify web access token");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
    
    /// <summary>
    /// Fetches TOTP secrets from the pre-scraped secrets repository.
    /// </summary>
    private async Task<TotpSecret?> GetTotpSecretAsync(CancellationToken cancellationToken)
    {
        // Return cached secret if fresh (cache for 1 hour)
        if (_cachedTotpSecret != null && DateTime.UtcNow - _totpSecretFetchedAt < TimeSpan.FromHours(1))
        {
            return _cachedTotpSecret;
        }
        
        try
        {
            _logger.LogDebug("Fetching TOTP secrets from {Url}", TotpSecretsUrl);
            
            var response = await _webApiClient.GetAsync(TotpSecretsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch TOTP secrets: {StatusCode}", response.StatusCode);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var secrets = JsonSerializer.Deserialize<TotpSecret[]>(json);
            
            if (secrets == null || secrets.Length == 0)
            {
                _logger.LogError("No TOTP secrets found in response");
                return null;
            }
            
            // Use the newest version
            _cachedTotpSecret = secrets.OrderByDescending(s => s.Version).First();
            _totpSecretFetchedAt = DateTime.UtcNow;
            
            _logger.LogDebug("Got TOTP secret version {Version}", _cachedTotpSecret.Version);
            return _cachedTotpSecret;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TOTP secrets");
            return null;
        }
    }
    
    /// <summary>
    /// Generates a TOTP code using the secret and server time.
    /// Based on the Jellyfin plugin implementation.
    /// </summary>
    private async Task<(string Otp, long ServerTime)?> GenerateTotpAsync(TotpSecret secret, CancellationToken cancellationToken)
    {
        try
        {
            // Get server time from Spotify via HEAD request
            var headRequest = new HttpRequestMessage(HttpMethod.Head, SpotifyBaseUrl);
            var response = await _webApiClient.SendAsync(headRequest, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get Spotify server time: {StatusCode}", response.StatusCode);
                return null;
            }
            
            var serverTime = response.Headers.Date?.ToUnixTimeSeconds();
            if (serverTime == null)
            {
                _logger.LogError("No Date header in Spotify response");
                return null;
            }
            
            // Compute secret from cipher bytes
            // The secret bytes need to be transformed: XOR each byte with ((index % 33) + 9)
            var cipherBytes = secret.Secret.ToArray();
            var transformedBytes = cipherBytes.Select((b, i) => (byte)(b ^ ((i % 33) + 9))).ToArray();
            
            // Convert to UTF-8 string representation then back to bytes for TOTP
            var transformedString = string.Join("", transformedBytes.Select(b => b.ToString()));
            var utf8Bytes = Encoding.UTF8.GetBytes(transformedString);
            
            // Generate TOTP
            var totp = new Totp(utf8Bytes, step: 30, totpSize: 6);
            var otp = totp.ComputeTotp(DateTime.UnixEpoch.AddSeconds(serverTime.Value));
            
            _logger.LogDebug("Generated TOTP for server time {ServerTime}", serverTime.Value);
            return (otp, serverTime.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating TOTP");
            return null;
        }
    }
    
    /// <summary>
    /// Fetches a playlist with all its tracks from Spotify using the GraphQL API.
    /// This matches the approach used by the Jellyfin Spotify Import plugin.
    /// </summary>
    /// <param name="playlistId">Spotify playlist ID or URI</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Playlist with tracks in correct order, or null if not found</returns>
    public async Task<SpotifyPlaylist?> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        // Extract ID from URI if needed (spotify:playlist:xxxxx or https://open.spotify.com/playlist/xxxxx)
        playlistId = ExtractPlaylistId(playlistId);
        
        var token = await GetWebAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Cannot fetch playlist without access token");
            return null;
        }
        
        try
        {
            // Use GraphQL API (same as Jellyfin plugin) - more reliable and less rate-limited
            return await FetchPlaylistViaGraphQLAsync(playlistId, token, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching playlist {PlaylistId}", playlistId);
            return null;
        }
    }
    
    /// <summary>
    /// Fetch playlist using Spotify's GraphQL API (api-partner.spotify.com/pathfinder/v1/query)
    /// This is the same approach used by the Jellyfin Spotify Import plugin
    /// </summary>
    private async Task<SpotifyPlaylist?> FetchPlaylistViaGraphQLAsync(
        string playlistId,
        string token,
        CancellationToken cancellationToken)
    {
        const int pageLimit = 50;
        var offset = 0;
        var totalTrackCount = pageLimit;
        var tracks = new List<SpotifyPlaylistTrack>();
        
        SpotifyPlaylist? playlist = null;
        
        while (tracks.Count < totalTrackCount && offset < totalTrackCount)
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            // Build GraphQL query URL (same as Jellyfin plugin)
            var queryParams = new Dictionary<string, string>
            {
                { "operationName", "fetchPlaylist" },
                { "variables", $"{{\"uri\":\"spotify:playlist:{playlistId}\",\"offset\":{offset},\"limit\":{pageLimit}}}" },
                { "extensions", "{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"19ff1327c29e99c208c86d7a9d8f1929cfdf3d3202a0ff4253c821f1901aa94d\"}}" }
            };
            
            var queryString = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{WebApiBase}/query?{queryString}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _webApiClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch playlist via GraphQL: {StatusCode}", response.StatusCode);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("playlistV2", out var playlistV2))
            {
                _logger.LogError("Invalid GraphQL response structure");
                return null;
            }
            
            // Parse playlist metadata on first iteration
            if (playlist == null)
            {
                playlist = ParseGraphQLPlaylist(playlistV2, playlistId);
                if (playlist == null) return null;
            }
            
            // Parse tracks from this page
            if (playlistV2.TryGetProperty("content", out var content))
            {
                if (content.TryGetProperty("totalCount", out var totalCount))
                {
                    totalTrackCount = totalCount.GetInt32();
                }
                
                if (content.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var track = ParseGraphQLTrack(item, offset + tracks.Count);
                        if (track != null)
                        {
                            tracks.Add(track);
                        }
                    }
                }
            }
            
            offset += pageLimit;
        }
        
        if (playlist != null)
        {
            playlist.Tracks = tracks;
            playlist.TotalTracks = tracks.Count;
            _logger.LogInformation("Fetched playlist '{Name}' with {Count} tracks via GraphQL", playlist.Name, tracks.Count);
        }
        
        return playlist;
    }
    
    private SpotifyPlaylist? ParseGraphQLPlaylist(JsonElement playlistV2, string playlistId)
    {
        try
        {
            var name = playlistV2.TryGetProperty("name", out var n) ? n.GetString() : "Unknown Playlist";
            var description = playlistV2.TryGetProperty("description", out var d) ? d.GetString() : null;
            
            string? ownerName = null;
            if (playlistV2.TryGetProperty("ownerV2", out var owner) &&
                owner.TryGetProperty("data", out var ownerData) &&
                ownerData.TryGetProperty("name", out var ownerNameProp))
            {
                ownerName = ownerNameProp.GetString();
            }
            
            return new SpotifyPlaylist
            {
                SpotifyId = playlistId,
                Name = name ?? "Unknown Playlist",
                Description = description,
                OwnerName = ownerName,
                FetchedAt = DateTime.UtcNow,
                Tracks = new List<SpotifyPlaylistTrack>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse GraphQL playlist metadata");
            return null;
        }
    }
    
    private SpotifyPlaylistTrack? ParseGraphQLTrack(JsonElement item, int position)
    {
        try
        {
            if (!item.TryGetProperty("itemV2", out var itemV2) ||
                !itemV2.TryGetProperty("data", out var data))
            {
                return null;
            }
            
            var trackId = data.TryGetProperty("uri", out var uri) ? uri.GetString()?.Replace("spotify:track:", "") : null;
            var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
            
            if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(name))
            {
                return null;
            }
            
            // Parse artists
            var artists = new List<string>();
            if (data.TryGetProperty("artists", out var artistsObj) &&
                artistsObj.TryGetProperty("items", out var artistItems))
            {
                foreach (var artist in artistItems.EnumerateArray())
                {
                    if (artist.TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var artistName))
                    {
                        var artistNameStr = artistName.GetString();
                        if (!string.IsNullOrEmpty(artistNameStr))
                        {
                            artists.Add(artistNameStr);
                        }
                    }
                }
            }
            
            // Parse album
            string? albumName = null;
            if (data.TryGetProperty("albumOfTrack", out var album) &&
                album.TryGetProperty("name", out var albumNameProp))
            {
                albumName = albumNameProp.GetString();
            }
            
            // Parse duration
            int durationMs = 0;
            if (data.TryGetProperty("trackDuration", out var duration) &&
                duration.TryGetProperty("totalMilliseconds", out var durationMsProp))
            {
                durationMs = durationMsProp.GetInt32();
            }
            
            // Parse album art
            string? albumArtUrl = null;
            if (data.TryGetProperty("albumOfTrack", out var albumOfTrack) &&
                albumOfTrack.TryGetProperty("coverArt", out var coverArt) &&
                coverArt.TryGetProperty("sources", out var sources) &&
                sources.GetArrayLength() > 0)
            {
                var firstSource = sources[0];
                if (firstSource.TryGetProperty("url", out var urlProp))
                {
                    albumArtUrl = urlProp.GetString();
                }
            }
            
            return new SpotifyPlaylistTrack
            {
                SpotifyId = trackId,
                Title = name,
                Artists = artists,
                Album = albumName ?? string.Empty,
                DurationMs = durationMs,
                Position = position,
                AlbumArtUrl = albumArtUrl,
                Isrc = null // GraphQL doesn't return ISRC, we'll fetch it separately if needed
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse GraphQL track");
            return null;
        }
    }
    
    private async Task<SpotifyPlaylist?> FetchPlaylistMetadataAsync(
        string playlistId, 
        string token, 
        CancellationToken cancellationToken)
    {
        var url = $"{OfficialApiBase}/playlists/{playlistId}?fields=id,name,description,owner(display_name,id),images,collaborative,public,snapshot_id,tracks.total";
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch playlist metadata: {StatusCode}", response.StatusCode);
            return null;
        }
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        var playlist = new SpotifyPlaylist
        {
            SpotifyId = root.GetProperty("id").GetString() ?? playlistId,
            Name = root.GetProperty("name").GetString() ?? "Unknown Playlist",
            Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            SnapshotId = root.TryGetProperty("snapshot_id", out var snap) ? snap.GetString() : null,
            Collaborative = root.TryGetProperty("collaborative", out var collab) && collab.GetBoolean(),
            Public = root.TryGetProperty("public", out var pub) && pub.ValueKind != JsonValueKind.Null && pub.GetBoolean(),
            FetchedAt = DateTime.UtcNow
        };
        
        if (root.TryGetProperty("owner", out var owner))
        {
            playlist.OwnerName = owner.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            playlist.OwnerId = owner.TryGetProperty("id", out var oid) ? oid.GetString() : null;
        }
        
        if (root.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
        {
            playlist.ImageUrl = images[0].GetProperty("url").GetString();
        }
        
        if (root.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("total", out var total))
        {
            playlist.TotalTracks = total.GetInt32();
        }
        
        return playlist;
    }
    
    private async Task<List<SpotifyPlaylistTrack>> FetchAllPlaylistTracksAsync(
        string playlistId,
        string token,
        CancellationToken cancellationToken)
    {
        var allTracks = new List<SpotifyPlaylistTrack>();
        var offset = 0;
        const int limit = 100; // Spotify's max
        
        while (true)
        {
            var tracks = await FetchPlaylistTracksPageAsync(playlistId, token, offset, limit, cancellationToken);
            if (tracks == null || tracks.Count == 0) break;
            
            allTracks.AddRange(tracks);
            
            if (tracks.Count < limit) break;
            
            offset += limit;
            
            // Rate limiting
            if (_settings.RateLimitDelayMs > 0)
            {
                await Task.Delay(_settings.RateLimitDelayMs, cancellationToken);
            }
        }
        
        return allTracks;
    }
    
    private async Task<List<SpotifyPlaylistTrack>?> FetchPlaylistTracksPageAsync(
        string playlistId,
        string token,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        // Request fields needed for matching and ordering
        var fields = "items(added_at,track(id,name,album(id,name,images,release_date),artists(id,name),duration_ms,explicit,popularity,preview_url,disc_number,track_number,external_ids))";
        var url = $"{OfficialApiBase}/playlists/{playlistId}/tracks?offset={offset}&limit={limit}&fields={fields}";
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch playlist tracks: {StatusCode}", response.StatusCode);
            return null;
        }
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("items", out var items))
        {
            return new List<SpotifyPlaylistTrack>();
        }
        
        var tracks = new List<SpotifyPlaylistTrack>();
        var position = offset;
        
        foreach (var item in items.EnumerateArray())
        {
            // Skip null tracks (can happen with deleted/unavailable tracks)
            if (!item.TryGetProperty("track", out var trackElement) || 
                trackElement.ValueKind == JsonValueKind.Null)
            {
                position++;
                continue;
            }
            
            var track = ParseTrack(trackElement, position);
            
            // Parse added_at timestamp
            if (item.TryGetProperty("added_at", out var addedAt) && 
                addedAt.ValueKind != JsonValueKind.Null)
            {
                var addedAtStr = addedAt.GetString();
                if (DateTime.TryParse(addedAtStr, out var addedAtDate))
                {
                    track.AddedAt = addedAtDate;
                }
            }
            
            tracks.Add(track);
            position++;
        }
        
        return tracks;
    }
    
    private SpotifyPlaylistTrack ParseTrack(JsonElement track, int position)
    {
        var result = new SpotifyPlaylistTrack
        {
            Position = position,
            SpotifyId = track.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Title = track.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            DurationMs = track.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0,
            Explicit = track.TryGetProperty("explicit", out var exp) && exp.GetBoolean(),
            Popularity = track.TryGetProperty("popularity", out var pop) ? pop.GetInt32() : 0,
            PreviewUrl = track.TryGetProperty("preview_url", out var prev) && prev.ValueKind != JsonValueKind.Null 
                ? prev.GetString() : null,
            DiscNumber = track.TryGetProperty("disc_number", out var disc) ? disc.GetInt32() : 1,
            TrackNumber = track.TryGetProperty("track_number", out var tn) ? tn.GetInt32() : 1
        };
        
        // Parse album
        if (track.TryGetProperty("album", out var album))
        {
            result.Album = album.TryGetProperty("name", out var albumName) 
                ? albumName.GetString() ?? "" : "";
            result.AlbumId = album.TryGetProperty("id", out var albumId) 
                ? albumId.GetString() ?? "" : "";
            result.ReleaseDate = album.TryGetProperty("release_date", out var rd) 
                ? rd.GetString() : null;
                
            if (album.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
            {
                result.AlbumArtUrl = images[0].GetProperty("url").GetString();
            }
        }
        
        // Parse artists
        if (track.TryGetProperty("artists", out var artists))
        {
            foreach (var artist in artists.EnumerateArray())
            {
                if (artist.TryGetProperty("name", out var artistName))
                {
                    result.Artists.Add(artistName.GetString() ?? "");
                }
                if (artist.TryGetProperty("id", out var artistId))
                {
                    result.ArtistIds.Add(artistId.GetString() ?? "");
                }
            }
        }
        
        // Parse ISRC from external_ids
        if (track.TryGetProperty("external_ids", out var externalIds) &&
            externalIds.TryGetProperty("isrc", out var isrc))
        {
            result.Isrc = isrc.GetString();
        }
        
        return result;
    }
    
    /// <summary>
    /// Searches for a user's playlists by name.
    /// Useful for finding playlists like "Release Radar" or "Discover Weekly" by their names.
    /// </summary>
    public async Task<List<SpotifyPlaylist>> SearchUserPlaylistsAsync(
        string searchName, 
        CancellationToken cancellationToken = default)
    {
        var token = await GetWebAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return new List<SpotifyPlaylist>();
        }
        
        try
        {
            var playlists = new List<SpotifyPlaylist>();
            var offset = 0;
            const int limit = 50;
            
            while (true)
            {
                var url = $"{OfficialApiBase}/me/playlists?offset={offset}&limit={limit}";
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                
                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) break;
                
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                    break;
                
                foreach (var item in items.EnumerateArray())
                {
                    var itemName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    
                    // Check if name matches (case-insensitive)
                    if (itemName.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                    {
                        playlists.Add(new SpotifyPlaylist
                        {
                            SpotifyId = item.TryGetProperty("id", out var itemId) ? itemId.GetString() ?? "" : "",
                            Name = itemName,
                            Description = item.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                            TotalTracks = item.TryGetProperty("tracks", out var tracks) && 
                                          tracks.TryGetProperty("total", out var total) 
                                ? total.GetInt32() : 0,
                            SnapshotId = item.TryGetProperty("snapshot_id", out var snap) ? snap.GetString() : null
                        });
                    }
                }
                
                if (items.GetArrayLength() < limit) break;
                offset += limit;
                
                if (_settings.RateLimitDelayMs > 0)
                {
                    await Task.Delay(_settings.RateLimitDelayMs, cancellationToken);
                }
            }
            
            return playlists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching user playlists for '{SearchName}'", searchName);
            return new List<SpotifyPlaylist>();
        }
    }
    
    /// <summary>
    /// Gets the current user's profile to verify authentication is working.
    /// </summary>
    public async Task<(bool Success, string? UserId, string? DisplayName)> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await GetWebAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return (false, null, null);
        }
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{OfficialApiBase}/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Spotify /me endpoint returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return (false, null, null);
            }
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            var userId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            var displayName = root.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            
            return (true, userId, displayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current Spotify user");
            return (false, null, null);
        }
    }
    
    private static string ExtractPlaylistId(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // Handle spotify:playlist:xxxxx format
        if (input.StartsWith("spotify:playlist:"))
        {
            return input.Substring("spotify:playlist:".Length);
        }
        
        // Handle https://open.spotify.com/playlist/xxxxx format
        if (input.Contains("open.spotify.com/playlist/"))
        {
            var start = input.IndexOf("/playlist/") + "/playlist/".Length;
            var end = input.IndexOf('?', start);
            return end > 0 ? input.Substring(start, end - start) : input.Substring(start);
        }
        
        return input;
    }
    
    public void Dispose()
    {
        _httpClient.Dispose();
        _webApiClient.Dispose();
        _tokenLock.Dispose();
    }
    
    // Internal classes for JSON deserialization
    private class SpotifyTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;
        
        [JsonPropertyName("accessTokenExpirationTimestampMs")]
        public long ExpirationTimestampMs { get; set; }
        
        [JsonPropertyName("isAnonymous")]
        public bool IsAnonymous { get; set; }
        
        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = string.Empty;
    }
    
    private class TotpSecret
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }
        
        [JsonPropertyName("secret")]
        public List<byte> Secret { get; set; } = new();
    }
}
