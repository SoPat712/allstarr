using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using System.Net.Http.Headers;
using System.Text.Json;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Handles proxying requests to the Jellyfin server and authentication.
/// </summary>
public class JellyfinProxyService
{
    private readonly HttpClient _httpClient;
    private readonly JellyfinSettings _settings;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<JellyfinProxyService> _logger;
    private readonly RedisCacheService _cache;
    private string? _cachedMusicLibraryId;
    private bool _libraryIdDetected = false;

    // Expose HttpClient for direct streaming scenarios
    public HttpClient HttpClient => _httpClient;

    public JellyfinProxyService(
        IHttpClientFactory httpClientFactory,
        IOptions<JellyfinSettings> settings,
        IHttpContextAccessor httpContextAccessor,
        ILogger<JellyfinProxyService> logger,
        RedisCacheService cache)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Gets the music library ID, auto-detecting it if not configured.
    /// </summary>
    private async Task<string?> GetMusicLibraryIdAsync()
    {
        // Return configured library ID if set
        if (!string.IsNullOrEmpty(_settings.LibraryId))
        {
            return _settings.LibraryId;
        }

        // Return cached value if already detected
        if (_libraryIdDetected)
        {
            return _cachedMusicLibraryId;
        }

        // Auto-detect music library ID
        try
        {
            _logger.LogInformation("Auto-detecting music library ID...");
            _cachedMusicLibraryId = await GetMusicLibraryIdInternalAsync();
            _libraryIdDetected = true;

            if (!string.IsNullOrEmpty(_cachedMusicLibraryId))
            {
                _logger.LogInformation("Music library auto-detected: {LibraryId}", _cachedMusicLibraryId);
            }
            else
            {
                _logger.LogWarning("Could not auto-detect music library. All content types will be visible. Set JELLYFIN_LIBRARY_ID to filter to music only.");
            }

            return _cachedMusicLibraryId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-detect music library ID");
            _libraryIdDetected = true; // Don't keep trying
            return null;
        }
    }

    /// <summary>
    /// Public method for controllers to get the music library ID for filtering.
    /// </summary>
    public async Task<string?> GetMusicLibraryIdForFilteringAsync()
    {
        return await GetMusicLibraryIdAsync();
    }

    /// <summary>
    /// Gets the authorization header value for Jellyfin API requests.
    /// </summary>
    private string GetAuthorizationHeader()
    {
        return $"MediaBrowser Client=\"{_settings.ClientName}\", " +
               $"Device=\"{_settings.DeviceName}\", " +
               $"DeviceId=\"{_settings.DeviceId}\", " +
               $"Version=\"{_settings.ClientVersion}\", " +
               $"Token=\"{_settings.ApiKey}\"";
    }

    /// <summary>
    /// Sends a GET request to the Jellyfin server.
    /// If endpoint already contains query parameters, they will be preserved and merged with queryParams.
    /// </summary>
    public async Task<JsonDocument?> GetJsonAsync(string endpoint, Dictionary<string, string>? queryParams = null, IHeaderDictionary? clientHeaders = null)
    {
        // If endpoint contains query string, parse and merge with queryParams
        if (endpoint.Contains('?'))
        {
            var parts = endpoint.Split('?', 2);
            var baseEndpoint = parts[0];
            var existingQuery = parts[1];
            
            // Parse existing query string
            var mergedParams = new Dictionary<string, string>();
            foreach (var param in existingQuery.Split('&'))
            {
                var kv = param.Split('=', 2);
                if (kv.Length == 2)
                {
                    mergedParams[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                }
            }
            
            // Merge with provided queryParams (provided params take precedence)
            if (queryParams != null)
            {
                foreach (var kv in queryParams)
                {
                    mergedParams[kv.Key] = kv.Value;
                }
            }
            
            var url = BuildUrl(baseEndpoint, mergedParams);
            return await GetJsonAsyncInternal(url, clientHeaders);
        }
        
        var finalUrl = BuildUrl(endpoint, queryParams);
        return await GetJsonAsyncInternal(finalUrl, clientHeaders);
    }
    
    private async Task<JsonDocument?> GetJsonAsyncInternal(string url, IHeaderDictionary? clientHeaders)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        bool authHeaderAdded = false;
        
        // Forward authentication headers from client if provided
        if (clientHeaders != null && clientHeaders.Count > 0)
        {
            // Try X-Emby-Authorization first (case-insensitive)
            foreach (var header in clientHeaders)
            {
                if (header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var headerValue = header.Value.ToString();
                    request.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                    authHeaderAdded = true;
                    _logger.LogInformation("✓ Forwarded X-Emby-Authorization: {Value}", headerValue);
                    break;
                }
            }
            
            // If no X-Emby-Authorization, check if Authorization header contains MediaBrowser format
            // Some clients send it as "Authorization" instead of "X-Emby-Authorization"
            if (!authHeaderAdded)
            {
                foreach (var header in clientHeaders)
                {
                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        var headerValue = header.Value.ToString();
                        
                        // Check if it's MediaBrowser/Jellyfin format (contains "MediaBrowser" or "Token=")
                        if (headerValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase) || 
                            headerValue.Contains("Token=", StringComparison.OrdinalIgnoreCase))
                        {
                            // Forward as X-Emby-Authorization (Jellyfin's expected header)
                            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                            authHeaderAdded = true;
                            _logger.LogInformation("✓ Converted Authorization to X-Emby-Authorization: {Value}", headerValue);
                        }
                        else
                        {
                            // Standard Bearer token - forward as-is
                            request.Headers.TryAddWithoutValidation("Authorization", headerValue);
                            authHeaderAdded = true;
                            _logger.LogInformation("✓ Forwarded Authorization (Bearer): {Value}", headerValue);
                        }
                        break;
                    }
                }
            }
            
            if (!authHeaderAdded)
            {
                _logger.LogWarning("✗ No auth header found. Available headers: {Headers}", 
                    string.Join(", ", clientHeaders.Select(h => $"{h.Key}={h.Value}")));
            }
        }
        else
        {
            _logger.LogWarning("✗ No client headers provided for {Url}", url);
        }
        
        // Use API key if no valid client auth was found
        if (!authHeaderAdded)
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey))
            {
                request.Headers.Add("Authorization", GetAuthorizationHeader());
                _logger.LogInformation("→ Using API key for {Url}", url);
            }
            else
            {
                _logger.LogWarning("✗ No authentication available for {Url} - request will fail", url);
            }
        }
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request);
        
        // Always parse the response, even for errors
        // The caller needs to see 401s so the client can re-authenticate
        var content = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Jellyfin returned 401 Unauthorized for {Url} - passing through to client", url);
            }
            else
            {
                _logger.LogWarning("Jellyfin request failed: {StatusCode} for {Url}", response.StatusCode, url);
            }
            
            // Return null so caller knows request failed
            // TODO: We should return the status code too so caller can pass it through
            return null;
        }

        return JsonDocument.Parse(content);
    }

    /// <summary>
    /// Sends a POST request to the Jellyfin server with JSON body.
    /// Forwards client headers for authentication passthrough.
    /// </summary>
    public async Task<JsonDocument?> PostJsonAsync(string endpoint, string body, IHeaderDictionary clientHeaders)
    {
        var url = BuildUrl(endpoint, null);
        
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        
        // Handle special case for playback endpoints - Jellyfin expects wrapped body
        var bodyToSend = body;
        if (!string.IsNullOrWhiteSpace(body))
        {
            // Check if this is a playback progress endpoint
            if (endpoint.Contains("Sessions/Playing/Progress", StringComparison.OrdinalIgnoreCase))
            {
                // Wrap the body in playbackProgressInfo field
                bodyToSend = $"{{\"playbackProgressInfo\":{body}}}";
                _logger.LogDebug("Wrapped body for playback progress endpoint");
            }
            else if (endpoint.Contains("Sessions/Playing/Stopped", StringComparison.OrdinalIgnoreCase))
            {
                // Wrap the body in playbackStopInfo field
                bodyToSend = $"{{\"playbackStopInfo\":{body}}}";
                _logger.LogDebug("Wrapped body for playback stopped endpoint");
            }
            else if (endpoint.Contains("Sessions/Playing", StringComparison.OrdinalIgnoreCase) && 
                     !endpoint.Contains("Progress", StringComparison.OrdinalIgnoreCase) &&
                     !endpoint.Contains("Stopped", StringComparison.OrdinalIgnoreCase))
            {
                // Wrap the body in playbackStartInfo field for /Sessions/Playing
                bodyToSend = $"{{\"playbackStartInfo\":{body}}}";
                _logger.LogDebug("Wrapped body for playback start endpoint");
            }
        }
        else
        {
            bodyToSend = "{}";
            _logger.LogWarning("POST body was empty for {Url}, sending empty JSON object", url);
        }
        
        request.Content = new StringContent(bodyToSend, System.Text.Encoding.UTF8, "application/json");
        
        bool authHeaderAdded = false;
        
        // Forward authentication headers from client (case-insensitive)
        foreach (var header in clientHeaders)
        {
            if (header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = header.Value.ToString();
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                authHeaderAdded = true;
                _logger.LogDebug("Forwarded X-Emby-Authorization from client");
                break;
            }
        }
        
        if (!authHeaderAdded)
        {
            foreach (var header in clientHeaders)
            {
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var headerValue = header.Value.ToString();
                    
                    // Check if it's MediaBrowser/Jellyfin format
                    if (headerValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase) || 
                        headerValue.Contains("Client=", StringComparison.OrdinalIgnoreCase))
                    {
                        // Forward as X-Emby-Authorization
                        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                        _logger.LogDebug("Converted Authorization to X-Emby-Authorization");
                    }
                    else
                    {
                        // Standard Bearer token
                        request.Headers.TryAddWithoutValidation("Authorization", headerValue);
                        _logger.LogDebug("Forwarded Authorization header");
                    }
                    authHeaderAdded = true;
                    break;
                }
            }
        }
        
        // For non-auth requests without headers, use API key
        // For auth requests, client MUST provide their own client info
        if (!authHeaderAdded && !endpoint.Contains("Authenticate", StringComparison.OrdinalIgnoreCase))
        {
            var clientAuthHeader = $"MediaBrowser Client=\"{_settings.ClientName}\", " +
                                   $"Device=\"{_settings.DeviceName}\", " +
                                   $"DeviceId=\"{_settings.DeviceId}\", " +
                                   $"Version=\"{_settings.ClientVersion}\"";
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", clientAuthHeader);
            _logger.LogDebug("Using server API key for non-auth request");
        }
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // DO NOT log the body for auth endpoints - it contains passwords!
        if (endpoint.Contains("Authenticate", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("POST to Jellyfin: {Url} (auth request - body not logged)", url);
        }
        else
        {
            _logger.LogInformation("POST to Jellyfin: {Url}, body length: {Length} bytes", url, bodyToSend.Length);
            
            // Log body content for playback endpoints to debug
            if (endpoint.Contains("Playing", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Sending body to Jellyfin: {Body}", bodyToSend);
            }
        }
        
        var response = await _httpClient.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Jellyfin POST request failed: {StatusCode} for {Url}. Response: {Response}", 
                response.StatusCode, url, errorContent);
            return null;
        }

        // Handle 204 No Content responses (e.g., /sessions/playing, /sessions/playing/progress)
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        
        // Handle empty responses
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }
        
        return JsonDocument.Parse(responseContent);
    }

    /// <summary>
    /// Sends a GET request and returns raw bytes (for images, audio streams).
    /// </summary>
    public async Task<(byte[] Body, string? ContentType)> GetBytesAsync(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(endpoint, queryParams);
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", GetAuthorizationHeader());

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();

        return (body, contentType);
    }

    /// <summary>
    /// Safely sends a GET request to the Jellyfin server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> GetBytesSafeAsync(
        string endpoint, 
        Dictionary<string, string>? queryParams = null)
    {
        try
        {
            var result = await GetBytesAsync(endpoint, queryParams);
            return (result.Body, result.ContentType, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get bytes from {Endpoint}", endpoint);
            return (null, null, false);
        }
    }

    /// <summary>
    /// Searches for items in Jellyfin.
    /// Uses configured or auto-detected LibraryId to filter search to music library only.
    /// </summary>
    public async Task<JsonDocument?> SearchAsync(
        string searchTerm,
        string[]? includeItemTypes = null,
        int limit = 20,
        bool recursive = true,
        IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["searchTerm"] = searchTerm,
            ["limit"] = limit.ToString(),
            ["recursive"] = recursive.ToString().ToLower(),
            ["fields"] = "PrimaryImageAspectRatio,MediaSources,Path,Genres,Studios,DateCreated,Overview,ProviderIds"
        };

        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        // Only filter search to music library if explicitly configured
        if (!string.IsNullOrEmpty(_settings.LibraryId))
        {
            queryParams["parentId"] = _settings.LibraryId;
            _logger.LogDebug("Searching within configured LibraryId {LibraryId}", _settings.LibraryId);
        }

        if (includeItemTypes != null && includeItemTypes.Length > 0)
        {
            queryParams["includeItemTypes"] = string.Join(",", includeItemTypes);
        }

        return await GetJsonAsync("Items", queryParams, clientHeaders);
    }

    /// <summary>
    /// Gets items from a specific parent (album, artist, playlist).
    /// </summary>
    public async Task<JsonDocument?> GetItemsAsync(
        string? parentId = null,
        string[]? includeItemTypes = null,
        string? sortBy = null,
        int? limit = null,
        int? startIndex = null,
        string? artistIds = null,
        IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["recursive"] = "true",
            ["fields"] = "PrimaryImageAspectRatio,MediaSources,Path,Genres,Studios,DateCreated,Overview,ProviderIds,ParentId"
        };

        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        if (!string.IsNullOrEmpty(parentId))
        {
            queryParams["parentId"] = parentId;
        }

        if (includeItemTypes != null && includeItemTypes.Length > 0)
        {
            queryParams["includeItemTypes"] = string.Join(",", includeItemTypes);
        }

        if (!string.IsNullOrEmpty(sortBy))
        {
            queryParams["sortBy"] = sortBy;
        }

        if (limit.HasValue)
        {
            queryParams["limit"] = limit.Value.ToString();
        }

        if (startIndex.HasValue)
        {
            queryParams["startIndex"] = startIndex.Value.ToString();
        }

        if (!string.IsNullOrEmpty(artistIds))
        {
            queryParams["artistIds"] = artistIds;
        }

        return await GetJsonAsync("Items", queryParams, clientHeaders);
    }

    /// <summary>
    /// Gets a single item by ID.
    /// </summary>
    public async Task<JsonDocument?> GetItemAsync(string itemId, IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>();
        
        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        return await GetJsonAsync($"Items/{itemId}", queryParams, clientHeaders);
    }

    /// <summary>
    /// Gets artists from the library.
    /// </summary>
    public async Task<JsonDocument?> GetArtistsAsync(
        string? searchTerm = null,
        int? limit = null,
        int? startIndex = null,
        IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["fields"] = "PrimaryImageAspectRatio,Genres,Overview"
        };

        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        if (!string.IsNullOrEmpty(searchTerm))
        {
            queryParams["searchTerm"] = searchTerm;
        }

        if (limit.HasValue)
        {
            queryParams["limit"] = limit.Value.ToString();
        }

        if (startIndex.HasValue)
        {
            queryParams["startIndex"] = startIndex.Value.ToString();
        }

        return await GetJsonAsync("Artists", queryParams, clientHeaders);
    }

    /// <summary>
    /// Gets an artist by name or ID.
    /// </summary>
    public async Task<JsonDocument?> GetArtistAsync(string artistIdOrName, IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>();
        
        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        // Try to get by ID first
        if (Guid.TryParse(artistIdOrName, out _))
        {
            return await GetJsonAsync($"Items/{artistIdOrName}", queryParams, clientHeaders);
        }

        // Otherwise search by name
        return await GetJsonAsync($"Artists/{Uri.EscapeDataString(artistIdOrName)}", queryParams, clientHeaders);
    }

    /// <summary>
    /// Streams audio from Jellyfin with range support.
    /// </summary>
    public async Task<IActionResult> StreamAudioAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return new ObjectResult(new { error = "HTTP context not available" })
                {
                    StatusCode = 500
                };
            }

            var incomingRequest = httpContext.Request;
            var outgoingResponse = httpContext.Response;

            // Build the stream URL - use static streaming for simplicity
            var queryParams = new Dictionary<string, string>
            {
                ["static"] = "true",
                ["mediaSourceId"] = itemId
            };

            var url = BuildUrl($"Audio/{itemId}/stream", queryParams);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", GetAuthorizationHeader());

            // Forward Range headers for progressive streaming
            if (incomingRequest.Headers.TryGetValue("Range", out var range))
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToArray());
            }

            if (incomingRequest.Headers.TryGetValue("If-Range", out var ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToArray());
            }

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new StatusCodeResult((int)response.StatusCode);
            }

            // Forward HTTP status code
            outgoingResponse.StatusCode = (int)response.StatusCode;

            // Forward streaming headers
            var streamingHeaders = new[] { "Accept-Ranges", "Content-Range", "Content-Length", "ETag", "Last-Modified" };
            foreach (var header in streamingHeaders)
            {
                if (response.Headers.TryGetValues(header, out var values) ||
                    response.Content.Headers.TryGetValues(header, out values))
                {
                    outgoingResponse.Headers[header] = values.ToArray();
                }
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

            return new FileStreamResult(stream, contentType)
            {
                EnableRangeProcessing = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming from Jellyfin item {ItemId}", itemId);
            return new ObjectResult(new { error = $"Error streaming: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }

    /// <summary>
    /// Gets the image for an item.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType)> GetImageAsync(
        string itemId,
        string imageType = "Primary",
        int? maxWidth = null,
        int? maxHeight = null)
    {
        // Build cache key
        var cacheKey = $"image:{itemId}:{imageType}:{maxWidth}:{maxHeight}";
        
        // Try cache first
        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            var parts = cached.Split('|', 2);
            if (parts.Length == 2)
            {
                var body = Convert.FromBase64String(parts[0]);
                var contentType = parts[1];
                return (body, contentType);
            }
        }

        var queryParams = new Dictionary<string, string>();

        if (maxWidth.HasValue)
        {
            queryParams["maxWidth"] = maxWidth.Value.ToString();
        }

        if (maxHeight.HasValue)
        {
            queryParams["maxHeight"] = maxHeight.Value.ToString();
        }

        var result = await GetBytesSafeAsync($"Items/{itemId}/Images/{imageType}", queryParams);
        
        // Cache for 7 days if successful
        if (result.Success && result.Body != null)
        {
            var cacheValue = $"{Convert.ToBase64String(result.Body)}|{result.ContentType}";
            await _cache.SetStringAsync(cacheKey, cacheValue, TimeSpan.FromDays(7));
        }
        
        return (result.Body, result.ContentType);
    }

    /// <summary>
    /// Tests connection to the Jellyfin server.
    /// </summary>
    public async Task<(bool Success, string? ServerName, string? Version)> TestConnectionAsync()
    {
        try
        {
            var result = await GetJsonAsync("System/Info/Public");
            if (result == null)
            {
                return (false, null, null);
            }

            var serverName = result.RootElement.TryGetProperty("ServerName", out var name) 
                ? name.GetString() 
                : null;
            var version = result.RootElement.TryGetProperty("Version", out var ver) 
                ? ver.GetString() 
                : null;

            return (true, serverName, version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test Jellyfin connection");
            return (false, null, null);
        }
    }

    /// <summary>
    /// Gets the music library ID from Jellyfin by querying media folders.
    /// </summary>
    private async Task<string?> GetMusicLibraryIdInternalAsync()
    {
        try
        {
            var queryParams = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_settings.UserId))
            {
                queryParams["userId"] = _settings.UserId;
            }

            var result = await GetJsonAsync("Library/MediaFolders", queryParams);
            if (result == null)
            {
                return null;
            }

            if (result.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var collectionType = item.TryGetProperty("CollectionType", out var ct) 
                        ? ct.GetString() 
                        : null;
                    
                    if (collectionType == "music")
                    {
                        return item.TryGetProperty("Id", out var id) 
                            ? id.GetString() 
                            : null;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get music library ID");
            return null;
        }
    }

    private string BuildUrl(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var baseUrl = _settings.Url?.TrimEnd('/') ?? "";
        var url = $"{baseUrl}/{endpoint}";

        if (queryParams != null && queryParams.Count > 0)
        {
            var query = string.Join("&", queryParams.Select(kv => 
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{query}";
        }

        return url;
    }
}
