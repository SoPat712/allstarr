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
    /// Returns the response body and HTTP status code.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsync(string endpoint, Dictionary<string, string>? queryParams = null, IHeaderDictionary? clientHeaders = null)
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
    
    private async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsyncInternal(string url, IHeaderDictionary? clientHeaders)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        // Forward client IP address to Jellyfin so it can identify the real client
        if (_httpContextAccessor.HttpContext != null)
        {
            var clientIp = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientIp))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp);
            }
        }
        
        bool authHeaderAdded = false;
        
        // Check if this is a browser request for static assets (favicon, etc.)
        bool isBrowserStaticRequest = url.Contains("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
                                      url.Contains("/web/", StringComparison.OrdinalIgnoreCase) ||
                                      (clientHeaders?.Any(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && 
                                                              h.Value.ToString().Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) == true &&
                                       clientHeaders?.Any(h => h.Key.Equals("sec-fetch-dest", StringComparison.OrdinalIgnoreCase) && 
                                                              (h.Value.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) ||
                                                               h.Value.ToString().Contains("document", StringComparison.OrdinalIgnoreCase))) == true);
        
        // Check if this is a public endpoint that doesn't require authentication
        bool isPublicEndpoint = url.Contains("/System/Info/Public", StringComparison.OrdinalIgnoreCase) ||
                               url.Contains("/Branding/", StringComparison.OrdinalIgnoreCase) ||
                               url.Contains("/Startup/", StringComparison.OrdinalIgnoreCase);
        
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
                    _logger.LogTrace("Forwarded X-Emby-Authorization header");
                    break;
                }
            }
            
            // Try X-Emby-Token (simpler format used by some clients)
            if (!authHeaderAdded)
            {
                foreach (var header in clientHeaders)
                {
                    if (header.Key.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase))
                    {
                        var headerValue = header.Value.ToString();
                        request.Headers.TryAddWithoutValidation("X-Emby-Token", headerValue);
                        authHeaderAdded = true;
                        _logger.LogTrace("Forwarded X-Emby-Token header");
                        break;
                    }
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
                            _logger.LogDebug("Converted Authorization to X-Emby-Authorization");
                        }
                        else
                        {
                            // Standard Bearer token - forward as-is
                            request.Headers.TryAddWithoutValidation("Authorization", headerValue);
                            authHeaderAdded = true;
                            _logger.LogTrace("Forwarded Authorization header");
                        }
                        break;
                    }
                }
            }
            
            // Check for api_key query parameter (some clients use this)
            if (!authHeaderAdded && url.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
            {
                authHeaderAdded = true; // It's in the URL, no need to add header
                _logger.LogTrace("Using api_key from query string");
            }
        }
        
        // Only log warnings for non-public, non-browser requests without auth
        if (!authHeaderAdded && !isBrowserStaticRequest && !isPublicEndpoint)
        {
            _logger.LogDebug("No client auth provided for {Url} - Jellyfin will handle authentication", url);
        }
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request);
        
        var statusCode = (int)response.StatusCode;
        
        // Always parse the response, even for errors
        // The caller needs to see 401s so the client can re-authenticate
        var content = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // 401 means token expired or invalid - client needs to re-authenticate
                _logger.LogDebug("Jellyfin returned 401 Unauthorized for {Url} - client should re-authenticate", url);
            }
            else if (!isBrowserStaticRequest && !isPublicEndpoint)
            {
                _logger.LogError("Jellyfin request failed: {StatusCode} for {Url}", response.StatusCode, url);
            }
            
            // Try to parse error response to pass through to client
            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    var errorDoc = JsonDocument.Parse(content);
                    return (errorDoc, statusCode);
                }
                catch
                {
                    // Not valid JSON, return null
                }
            }
            
            return (null, statusCode);
        }

        return (JsonDocument.Parse(content), statusCode);
    }

    /// <summary>
    /// Sends a POST request to the Jellyfin server with JSON body.
    /// Forwards client headers for authentication passthrough.
    /// Returns the response body and HTTP status code.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> PostJsonAsync(string endpoint, string body, IHeaderDictionary clientHeaders)
    {
        var url = BuildUrl(endpoint, null);
        
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        
        // Forward client IP address to Jellyfin so it can identify the real client
        if (_httpContextAccessor.HttpContext != null)
        {
            var clientIp = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientIp))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp);
            }
        }
        
        // Handle special case for playback endpoints
        // NOTE: Jellyfin API expects PlaybackStartInfo/PlaybackProgressInfo/PlaybackStopInfo 
        // DIRECTLY as the body, NOT wrapped in a field. Do NOT wrap the body.
        var bodyToSend = body;
        if (string.IsNullOrWhiteSpace(body))
        {
            bodyToSend = "{}";
            _logger.LogWarning("POST body was empty for {Url}, sending empty JSON object", url);
        }
        
        request.Content = new StringContent(bodyToSend, System.Text.Encoding.UTF8, "application/json");
        
        bool authHeaderAdded = false;
        bool isAuthEndpoint = endpoint.Contains("Authenticate", StringComparison.OrdinalIgnoreCase);
        
        // Forward authentication headers from client (case-insensitive)
        // Try X-Emby-Authorization first
        foreach (var header in clientHeaders)
        {
            if (header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = header.Value.ToString();
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", headerValue);
                authHeaderAdded = true;
                _logger.LogTrace("Forwarded X-Emby-Authorization header");
                break;
            }
        }
        
        // Try X-Emby-Token
        if (!authHeaderAdded)
        {
            foreach (var header in clientHeaders)
            {
                if (header.Key.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase))
                {
                    var headerValue = header.Value.ToString();
                    request.Headers.TryAddWithoutValidation("X-Emby-Token", headerValue);
                    authHeaderAdded = true;
                    _logger.LogTrace("Forwarded X-Emby-Token header");
                    break;
                }
            }
        }
        
        // Try Authorization header
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
                        _logger.LogTrace("Forwarded Authorization header");
                    }
                    authHeaderAdded = true;
                    break;
                }
            }
        }
        
        // For authentication endpoints, credentials are in the body, not headers
        // For other endpoints without auth, let Jellyfin reject the request
        if (!authHeaderAdded && !isAuthEndpoint)
        {
            _logger.LogDebug("No client auth provided for POST {Url} - Jellyfin will handle authentication", url);
        }
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // DO NOT log the body for auth endpoints - it contains passwords!
        if (isAuthEndpoint)
        {
            _logger.LogDebug("POST to Jellyfin: {Url} (auth request - body not logged)", url);
        }
        else
        {
            _logger.LogTrace("POST to Jellyfin: {Url}, body length: {Length} bytes", url, bodyToSend.Length);
        }
        
        var response = await _httpClient.SendAsync(request);
        
        var statusCode = (int)response.StatusCode;
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            
            // 401 is expected when tokens expire - don't spam logs
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogDebug("Jellyfin POST returned 401 for {Url} - client should re-authenticate", url);
            }
            else
            {
                _logger.LogError("Jellyfin POST request failed: {StatusCode} for {Url}. Response: {Response}", 
                    response.StatusCode, url, errorContent.Length > 200 ? errorContent[..200] + "..." : errorContent);
            }
            
            // Try to parse error response as JSON to pass through to client
            if (!string.IsNullOrWhiteSpace(errorContent))
            {
                try
                {
                    var errorDoc = JsonDocument.Parse(errorContent);
                    return (errorDoc, statusCode);
                }
                catch
                {
                    // Not valid JSON, return null
                }
            }
            
            return (null, statusCode);
        }

        // Log successful session-related responses
        if (endpoint.Contains("Sessions", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("Jellyfin responded {StatusCode} for {Endpoint}", statusCode, endpoint);
        }

        // Handle 204 No Content responses (e.g., /sessions/playing, /sessions/playing/progress)
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return (null, statusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        
        // Handle empty responses
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return (null, statusCode);
        }
        
        return (JsonDocument.Parse(responseContent), statusCode);
    }

    /// <summary>
    /// Sends a GET request and returns raw bytes (for images, audio streams).
    /// WARNING: This loads entire response into memory - use StreamAsync for large files!
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

        // Trigger GC for large files to prevent memory leaks
        if (body.Length > 1024 * 1024) // 1MB threshold
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }

        return (body, contentType);
    }

    /// <summary>
    /// Streams content directly without loading into memory (for large files like audio).
    /// </summary>
    public async Task<(Stream Stream, string? ContentType, long? ContentLength)> GetStreamAsync(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(endpoint, queryParams);
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", GetAuthorizationHeader());

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        var contentLength = response.Content.Headers.ContentLength;

        return (stream, contentType, contentLength);
    }

    /// <summary>
    /// Sends a DELETE request to the Jellyfin server.
    /// Forwards client headers for authentication passthrough.
    /// Returns the response body and HTTP status code.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> DeleteAsync(string endpoint, IHeaderDictionary clientHeaders)
    {
        var url = BuildUrl(endpoint, null);
        
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        
        // Forward client IP address to Jellyfin so it can identify the real client
        if (_httpContextAccessor.HttpContext != null)
        {
            var clientIp = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientIp))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp);
            }
        }
        
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
        
        if (!authHeaderAdded)
        {
            _logger.LogDebug("No client auth provided for DELETE {Url} - forwarding without auth", url);
        }
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        _logger.LogDebug("DELETE to Jellyfin: {Url}", url);
        
        var response = await _httpClient.SendAsync(request);
        
        var statusCode = (int)response.StatusCode;
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Jellyfin DELETE request failed: {StatusCode} for {Url}. Response: {Response}", 
                response.StatusCode, url, errorContent);
            return (null, statusCode);
        }

        // Handle 204 No Content responses
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return (null, statusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        
        // Handle empty responses
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return (null, statusCode);
        }
        
        return (JsonDocument.Parse(responseContent), statusCode);
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
            _logger.LogError(ex, "Failed to get bytes from {Endpoint}", endpoint);
            return (null, null, false);
        }
    }

    /// <summary>
    /// Searches for items in Jellyfin.
    /// Uses configured or auto-detected LibraryId to filter search to music library only.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> SearchAsync(
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
            _logger.LogInformation("Searching within configured LibraryId {LibraryId}", _settings.LibraryId);
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
    public async Task<(JsonDocument? Body, int StatusCode)> GetItemsAsync(
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
    public async Task<(JsonDocument? Body, int StatusCode)> GetItemAsync(string itemId, IHeaderDictionary? clientHeaders = null)
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
    public async Task<(JsonDocument? Body, int StatusCode)> GetArtistsAsync(
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
    public async Task<(JsonDocument? Body, int StatusCode)> GetArtistAsync(string artistIdOrName, IHeaderDictionary? clientHeaders = null)
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
            await _cache.SetStringAsync(cacheKey, cacheValue, CacheExtensions.ProxyImagesTTL);
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
            var (result, statusCode) = await GetJsonAsync("System/Info/Public");
            if (result == null || statusCode != 200)
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

            var (result, statusCode) = await GetJsonAsync("Library/MediaFolders", queryParams);
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

    /// <summary>
    /// Sends a GET request to the Jellyfin server using the server's API key for internal operations.
    /// This should only be used for server-side operations, not for proxying client requests.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsyncInternal(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(endpoint, queryParams);
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        // Use server's API key for authentication
        var authHeader = GetAuthorizationHeader();
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", authHeader);
        
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request);
        var statusCode = (int)response.StatusCode;
        var content = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Jellyfin internal request returned {StatusCode} for {Url}: {Content}", 
                statusCode, url, content);
            return (null, statusCode);
        }

        try
        {
            var jsonDocument = JsonDocument.Parse(content);
            return (jsonDocument, statusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response from {Url}: {Content}", url, content);
            return (null, statusCode);
        }
    }
}
