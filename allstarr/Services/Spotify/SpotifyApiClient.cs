using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using Microsoft.Extensions.Options;
using OtpNet;
using allstarr.Services.Common;

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
    private readonly IApplicationCache? _cache;

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
        IOptions<SpotifyApiSettings> settings,
        IApplicationCache? cache = null)
    {
        _logger = logger;
        _settings = settings.Value;
        _cache = cache;

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
            _logger.LogInformation("No Spotify session cookie configured");
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
    public async Task<SpotifyPlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var token = await GetWebAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var pathfinder = new SpotifyPathfinderPlaylistClient(_webApiClient, _cache);
        var resource = new ProviderExternalResourceId(
            SpotifyPlaylistCapabilityAdapter.StableProviderId,
            ProviderResourceKind.Playlist,
            playlistId.Trim());
        var tracks = new List<SpotifyPlaylistTrack>();
        var seenTrackPositions = new HashSet<int>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        ProviderPlaylistSummary? summary = null;
        string? cursor = null;

        do
        {
            var outcome = await pathfinder.GetPlaylistTracksAsync(
                token,
                new ProviderPlaylistTracksRequest(
                    resource,
                    new ProviderPageRequest(100, cursor),
                    summary?.SourceRevision),
                cancellationToken);
            if (!outcome.IsSuccess)
            {
                _logger.LogWarning(
                    "Spotify Pathfinder track discovery stopped with {ErrorCode} after {Count} tracks for playlist {PlaylistId}",
                    outcome.Error?.Code ?? "unknown",
                    tracks.Count,
                    playlistId);
                return summary == null ? null : BuildCompatibilityPlaylist(summary, tracks, null);
            }

            var page = outcome.RequireValue();
            summary ??= page.Playlist;
            foreach (var item in page.Tracks.Items)
            {
                if (!seenTrackPositions.Add(item.Position))
                {
                    continue;
                }

                var metadata = item.Metadata;
                tracks.Add(new SpotifyPlaylistTrack
                {
                    SpotifyId = item.TrackId.Value,
                    Position = item.Position,
                    Title = metadata?.Title ?? string.Empty,
                    Album = metadata?.AlbumTitle ?? string.Empty,
                    AlbumId = metadata?.AlbumId?.Value ?? string.Empty,
                    Artists = metadata?.Artists.Select(artist => artist.Name).ToList() ?? [],
                    ArtistIds = metadata?.Artists
                        .Where(artist => artist.ArtistId != null)
                        .Select(artist => artist.ArtistId!.Value)
                        .ToList() ?? [],
                    Isrc = metadata?.Isrc,
                    DurationMs = metadata?.Duration is { } duration
                        ? (int)Math.Min(int.MaxValue, Math.Max(0, duration.TotalMilliseconds))
                        : 0,
                    Explicit = metadata?.IsExplicit ?? false,
                    AlbumArtUrl = metadata?.Artwork?.PublicUri?.ToString()
                });
            }

            cursor = page.Tracks.IsPartial ? page.Tracks.NextCursor : null;
        } while (!string.IsNullOrWhiteSpace(cursor) && seenCursors.Add(cursor));

        if (summary == null)
        {
            return null;
        }

        string? artworkUrl = null;
        if (summary.Artwork != null)
        {
            var artwork = await pathfinder.GetPlaylistArtworkUriAsync(
                token,
                summary.Artwork,
                cancellationToken);
            if (artwork.IsSuccess)
            {
                artworkUrl = artwork.RequireValue().ToString();
            }
        }

        return BuildCompatibilityPlaylist(summary, tracks, artworkUrl);
    }

    private static SpotifyPlaylist BuildCompatibilityPlaylist(
        ProviderPlaylistSummary summary,
        List<SpotifyPlaylistTrack> tracks,
        string? artworkUrl) => new()
    {
        SpotifyId = summary.Id.Value,
        Name = summary.Name,
        Description = summary.Description,
        OwnerId = summary.Owner.ProviderUserId,
        OwnerName = summary.Owner.DisplayName ?? summary.Owner.ProviderUserId,
        TotalTracks = summary.TrackCount ?? tracks.Count,
        ImageUrl = artworkUrl ?? summary.Artwork?.PublicUri?.ToString(),
        Tracks = tracks.OrderBy(track => track.Position).ToList(),
        SnapshotId = summary.SourceRevision,
        FetchedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Searches the selected account's playlists through the shared Pathfinder transport.
    /// </summary>
    public async Task<List<SpotifyPlaylist>> SearchUserPlaylistsAsync(
        string searchName,
        CancellationToken cancellationToken = default)
    {
        return await GetUserPlaylistsAsync(searchName, cancellationToken);
    }

    /// <summary>
    /// Gets all playlists from the user's library, optionally filtered by name.
    /// Uses GraphQL API which is less rate-limited than REST API.
    /// </summary>
    /// <param name="searchName">Optional name filter (case-insensitive). If null, returns all playlists.</param>
    public async Task<List<SpotifyPlaylist>> GetUserPlaylistsAsync(
        string? searchName = null,
        CancellationToken cancellationToken = default)
    {
        var token = await GetWebAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return [];
        }

        var pathfinder = new SpotifyPathfinderPlaylistClient(_webApiClient, _cache);
        var playlists = new List<SpotifyPlaylist>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var outcome = await pathfinder.GetUserPlaylistsAsync(
                token,
                new ProviderPageRequest(100, cursor),
                searchName,
                cancellationToken);
            if (!outcome.IsSuccess)
            {
                _logger.LogWarning(
                    "Spotify Pathfinder playlist discovery stopped with {ErrorCode} after {Count} playlists",
                    outcome.Error?.Code ?? "unknown",
                    playlists.Count);
                break;
            }

            var page = outcome.RequireValue();
            foreach (var item in page.Items)
            {
                if (!seenPlaylistIds.Add(item.Id.Value))
                {
                    continue;
                }

                playlists.Add(new SpotifyPlaylist
                {
                    SpotifyId = item.Id.Value,
                    Name = item.Name,
                    Description = item.Description,
                    TotalTracks = item.TrackCount ?? 0,
                    OwnerName = item.Owner.DisplayName ?? item.Owner.ProviderUserId,
                    ImageUrl = item.Artwork?.PublicUri?.ToString(),
                    SnapshotId = item.SourceRevision
                });
            }

            cursor = page.IsPartial ? page.NextCursor : null;
        } while (!string.IsNullOrWhiteSpace(cursor) && seenCursors.Add(cursor));

        _logger.LogDebug(
            "Found {Count} playlists{Filter} through the shared Spotify Pathfinder transport",
            playlists.Count,
            string.IsNullOrWhiteSpace(searchName) ? string.Empty : $" matching '{searchName}'");
        return playlists;
    }

    private static DateTime? TryGetSpotifyPlaylistCreatedAt(JsonElement playlistElement)
    {
        // Direct fields we may see across Spotify APIs.
        foreach (var candidateField in new[] { "createdAt", "created_at", "creationDate", "dateCreated" })
        {
            if (playlistElement.TryGetProperty(candidateField, out var candidate))
            {
                var parsed = ParseSpotifyDateElement(candidate);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        // GraphQL attributes as key/value entries.
        if (playlistElement.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (!attribute.TryGetProperty("key", out var keyProp) ||
                    !attribute.TryGetProperty("value", out var valueProp))
                {
                    continue;
                }

                var key = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!key.Contains("created", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = ParseSpotifyDateElement(valueProp);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        return null;
    }

    private static int TryGetSpotifyPlaylistItemCount(JsonElement playlistElement)
    {
        if (playlistElement.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("totalCount", out var totalTrackCount) &&
            TryParseSpotifyIntegerElement(totalTrackCount, out var contentCount))
        {
            return contentCount;
        }

        if (playlistElement.TryGetProperty("attributes", out var attributes))
        {
            if (attributes.ValueKind == JsonValueKind.Object &&
                attributes.TryGetProperty("itemCount", out var itemCountProp) &&
                TryParseSpotifyIntegerElement(itemCountProp, out var directAttributeCount))
            {
                return directAttributeCount;
            }

            if (attributes.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in attributes.EnumerateArray())
                {
                    if (attribute.ValueKind != JsonValueKind.Object ||
                        !attribute.TryGetProperty("key", out var keyProp) ||
                        keyProp.ValueKind != JsonValueKind.String ||
                        !attribute.TryGetProperty("value", out var valueProp))
                    {
                        continue;
                    }

                    var key = keyProp.GetString();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var normalizedKey = key.Replace("_", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(":", "", StringComparison.OrdinalIgnoreCase);
                    if (!normalizedKey.Contains("itemcount", StringComparison.OrdinalIgnoreCase) &&
                        !normalizedKey.Contains("trackcount", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryParseSpotifyIntegerElement(valueProp, out var attributeCount))
                    {
                        return attributeCount;
                    }
                }
            }
        }

        if (playlistElement.TryGetProperty("totalCount", out var directTotalCount) &&
            TryParseSpotifyIntegerElement(directTotalCount, out var totalCount))
        {
            return totalCount;
        }

        return 0;
    }

    private static DateTime? ParseSpotifyDateElement(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var stringValue = value.GetString();
                    return ParseSpotifyDateString(stringValue);
                }
            case JsonValueKind.Number:
                {
                    if (value.TryGetInt64(out var numericValue))
                    {
                        return ParseSpotifyUnixTimestamp(numericValue);
                    }

                    return null;
                }
            case JsonValueKind.Object:
                {
                    // Common GraphQL style: { "isoString": "..." }
                    if (value.TryGetProperty("isoString", out var isoString))
                    {
                        return ParseSpotifyDateElement(isoString);
                    }

                    if (value.TryGetProperty("value", out var nestedValue))
                    {
                        return ParseSpotifyDateElement(nestedValue);
                    }

                    if (value.TryGetProperty("timestampMs", out var timestampMs))
                    {
                        return ParseSpotifyDateElement(timestampMs);
                    }

                    if (value.TryGetProperty("milliseconds", out var milliseconds))
                    {
                        return ParseSpotifyDateElement(milliseconds);
                    }

                    return null;
                }
            default:
                return null;
        }
    }

    private static DateTime? ParseSpotifyDateString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedDateTimeOffset))
        {
            return parsedDateTimeOffset.UtcDateTime;
        }

        // Some attributes expose Unix timestamps as strings.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return ParseSpotifyUnixTimestamp(timestamp);
        }

        return null;
    }

    private static bool TryParseSpotifyIntegerElement(JsonElement value, out int parsed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out parsed);
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
            case JsonValueKind.Object:
                if (value.TryGetProperty("value", out var nestedValue) &&
                    TryParseSpotifyIntegerElement(nestedValue, out parsed))
                {
                    return true;
                }

                if (value.TryGetProperty("itemCount", out var itemCount) &&
                    TryParseSpotifyIntegerElement(itemCount, out parsed))
                {
                    return true;
                }

                if (value.TryGetProperty("totalCount", out var totalCount) &&
                    TryParseSpotifyIntegerElement(totalCount, out parsed))
                {
                    return true;
                }

                break;
        }

        parsed = 0;
        return false;
    }

    private static DateTime? ParseSpotifyUnixTimestamp(long value)
    {
        try
        {
            // Heuristic: values above this threshold are milliseconds.
            var isMilliseconds = value > 10_000_000_000;
            var utcDate = isMilliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
            return utcDate;
        }
        catch
        {
            return null;
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
