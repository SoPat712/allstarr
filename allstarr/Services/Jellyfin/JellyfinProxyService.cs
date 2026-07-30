using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Handles proxying requests to the Jellyfin server and authentication.
/// Uses a named HttpClient ("JellyfinBackend") with SocketsHttpHandler for
/// TCP connection pooling across scoped instances.
/// </summary>
public class JellyfinProxyService
{
    /// <summary>
    /// The IHttpClientFactory registration name for the Jellyfin backend client.
    /// Configured with SocketsHttpHandler for connection pooling in Program.cs.
    /// </summary>
    public const string HttpClientName = "JellyfinBackend";

    private readonly HttpClient _httpClient;
    private readonly JellyfinSettings _settings;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<JellyfinProxyService> _logger;
    private readonly IApplicationCache _cache;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly IConfiguration _configuration;
    private string? _cachedMusicLibraryId;
    private bool _libraryIdDetected = false;

    // Expose HttpClient for direct streaming scenarios
    public HttpClient HttpClient => _httpClient;

    public JellyfinProxyService(
        IHttpClientFactory httpClientFactory,
        IOptions<JellyfinSettings> settings,
        IHttpContextAccessor httpContextAccessor,
        ILogger<JellyfinProxyService> logger,
        IApplicationCache cache,
        IMediaAssetResolver mediaAssets,
        IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _settings = settings.Value;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _cache = cache;
        _mediaAssets = mediaAssets;
        _configuration = configuration;
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

            // Fast path: preserve the caller's raw query string exactly as provided.
            // This is required for endpoints that legitimately repeat keys like Fields=...
            if (queryParams == null || queryParams.Count == 0)
            {
                return await GetJsonAsyncInternal(BuildUrl(endpoint), clientHeaders);
            }

            var preservedParams = new List<string>();

            foreach (var param in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = param.Split('=', 2);
                var key = kv.Length > 0 ? Uri.UnescapeDataString(kv[0]) : string.Empty;

                // Explicit query params override every existing value for the same key.
                if (!string.IsNullOrEmpty(key) && queryParams.ContainsKey(key))
                {
                    continue;
                }

                preservedParams.Add(param);
            }

            var explicitParams = queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");

            var mergedQuery = string.Join("&", preservedParams.Concat(explicitParams));
            var url = string.IsNullOrEmpty(mergedQuery)
                ? BuildUrl(baseEndpoint)
                : $"{BuildUrl(baseEndpoint)}?{mergedQuery}";

            return await GetJsonAsyncInternal(url, clientHeaders);
        }

        var finalUrl = BuildUrl(endpoint, queryParams);
        return await GetJsonAsyncInternal(finalUrl, clientHeaders);
    }

    /// <summary>
    /// Relays an unhandled client request without assuming a JSON body or replacing client authentication.
    /// </summary>
    public async Task<HttpResponseMessage> SendPassthroughResponseAsync(
        HttpRequest incoming,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var method = new HttpMethod(incoming.Method);
        var url = BuildUrl(endpoint);
        using var request = new HttpRequestMessage(method, url);

        if (_httpContextAccessor.HttpContext?.Connection.RemoteIpAddress is { } remoteAddress)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteAddress.ToString());
            request.Headers.TryAddWithoutValidation("X-Real-IP", remoteAddress.ToString());
        }

        _ = AuthHeaderHelper.ForwardAuthHeaders(incoming.Headers, request);
        ForwardRelayRequestHeaders(incoming.Headers, request);

        if (MethodCanHaveBody(method) &&
            (incoming.ContentLength is > 0 || incoming.Headers.ContainsKey("Transfer-Encoding")))
        {
            request.Content = new StreamContent(incoming.Body);
            if (!string.IsNullOrWhiteSpace(incoming.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", incoming.ContentType);
            }

            foreach (var header in incoming.Headers.Where(header =>
                         header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) &&
                         !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                         !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        LogOutboundRequest(method, url);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogUpstreamFailure(method, response.StatusCode, url);
        }

        return response;
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsyncInternal(string url, IHeaderDictionary? clientHeaders)
    {
        using var request = CreateClientGetRequest(url, clientHeaders, out var isBrowserStaticRequest, out var isPublicEndpoint);

        LogOutboundRequest(HttpMethod.Get, url);

        var response = await _httpClient.SendAsync(request);

        var statusCode = (int)response.StatusCode;

        // Always parse the response, even for errors
        // The caller needs to see 401s so the client can re-authenticate
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (!isBrowserStaticRequest && !isPublicEndpoint)
            {
                LogUpstreamFailure(HttpMethod.Get, response.StatusCode, url);
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

    private HttpRequestMessage CreateClientGetRequest(
        string url,
        IHeaderDictionary? clientHeaders,
        out bool isBrowserStaticRequest,
        out bool isPublicEndpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

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

        // Check if this is a browser request for static assets (favicon, etc.)
        isBrowserStaticRequest = url.Contains("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
                                 url.Contains("/web/", StringComparison.OrdinalIgnoreCase) ||
                                 (clientHeaders?.Any(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) &&
                                                         h.Value.ToString().Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) == true &&
                                  clientHeaders?.Any(h => h.Key.Equals("sec-fetch-dest", StringComparison.OrdinalIgnoreCase) &&
                                                         (h.Value.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) ||
                                                          h.Value.ToString().Contains("document", StringComparison.OrdinalIgnoreCase))) == true);

        // Check if this is a public endpoint that doesn't require authentication
        isPublicEndpoint = url.Contains("/System/Info/Public", StringComparison.OrdinalIgnoreCase) ||
                           url.Contains("/Branding/", StringComparison.OrdinalIgnoreCase) ||
                           url.Contains("/Startup/", StringComparison.OrdinalIgnoreCase);

        var authHeaderAdded = false;

        // Forward authentication headers from client if provided
        if (clientHeaders != null && clientHeaders.Count > 0)
        {
            authHeaderAdded = AuthHeaderHelper.ForwardAuthHeaders(clientHeaders, request);

            if (authHeaderAdded)
            {
                _logger.LogTrace("Forwarded authentication headers");
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
            _logger.LogDebug(
                "No client auth provided for {Url} - Jellyfin will handle authentication",
                MaskSensitiveUrl(url));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ForwardRelayRequestHeaders(
        IHeaderDictionary incomingHeaders,
        HttpRequestMessage request)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Connection", "Keep-Alive", "Proxy-Authenticate",
            "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
            "Content-Length", "X-Forwarded-For", "X-Real-IP",
            "Authorization", "X-Emby-Authorization", "X-Emby-Token",
            "X-MediaBrowser-Token"
        };
        if (incomingHeaders.TryGetValue("Connection", out var connectionValues))
        {
            foreach (var name in connectionValues
                         .SelectMany(value => value?.Split(',') ?? [])
                         .Select(value => value.Trim())
                         .Where(value => value.Length > 0))
            {
                excluded.Add(name);
            }
        }

        foreach (var header in incomingHeaders)
        {
            if (!excluded.Contains(header.Key) &&
                !header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static bool MethodCanHaveBody(HttpMethod method) =>
        method != HttpMethod.Get &&
        method != HttpMethod.Head;

    /// <summary>
    /// Sends a POST request to the Jellyfin server with JSON body.
    /// Forwards client headers for authentication passthrough.
    /// Returns the response body and HTTP status code.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> PostJsonAsync(string endpoint, string body, IHeaderDictionary clientHeaders)
    {
        var bodyToSend = body;
        if (string.IsNullOrWhiteSpace(bodyToSend))
        {
            bodyToSend = "{}";
            _logger.LogWarning("POST body was empty; sending an empty JSON object");
        }

        return await SendAsync(HttpMethod.Post, endpoint, bodyToSend, clientHeaders, "application/json");
    }

    /// <summary>
    /// Sends an arbitrary HTTP request to Jellyfin while preserving the caller's method and body semantics.
    /// Intended for transparent proxy scenarios such as session control routes.
    /// </summary>
    public async Task<(JsonDocument? Body, int StatusCode)> SendAsync(
        HttpMethod method,
        string endpoint,
        string? body,
        IHeaderDictionary clientHeaders,
        string? contentType = null)
    {
        var url = BuildUrl(endpoint, null);
        var safeUrl = MaskSensitiveUrl(url);

        using var request = new HttpRequestMessage(method, url);

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

        if (body != null)
        {
            var requestContent = new StringContent(body, System.Text.Encoding.UTF8);
            try
            {
                requestContent.Headers.ContentType = !string.IsNullOrWhiteSpace(contentType)
                    ? MediaTypeHeaderValue.Parse(contentType)
                    : new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }
            catch (FormatException)
            {
                _logger.LogWarning("Invalid content type '{ContentType}' for {Method} {Url}; falling back to application/json",
                    contentType,
                    method,
                    safeUrl);
                requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            request.Content = requestContent;
        }

        var authHeaderAdded = AuthHeaderHelper.ForwardAuthHeaders(clientHeaders, request);
        var isAuthEndpoint = endpoint.Contains("Authenticate", StringComparison.OrdinalIgnoreCase);

        if (authHeaderAdded)
        {
            _logger.LogTrace("Forwarded authentication headers");
        }
        else if (!isAuthEndpoint)
        {
            _logger.LogDebug("No client auth provided for {Method} {Url} - Jellyfin will handle authentication", method, safeUrl);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        LogOutboundRequest(method, url);

        if (isAuthEndpoint)
        {
            _logger.LogDebug("{Method} to Jellyfin: {Url} (auth request - body not logged)", method, safeUrl);
        }
        else if (body == null)
        {
            _logger.LogTrace("{Method} to Jellyfin: {Url} (no request body)", method, safeUrl);
        }
        else
        {
            _logger.LogTrace("{Method} to Jellyfin: {Url}, body length: {Length} bytes", method, safeUrl, body.Length);
        }

        var response = await _httpClient.SendAsync(request);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            LogUpstreamFailure(method, response.StatusCode, url);

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

        if (endpoint.Contains("Sessions", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("Jellyfin responded {StatusCode} for {Method} {Url}", statusCode, method, safeUrl);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return (null, statusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
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

        LogOutboundRequest(HttpMethod.Get, url);

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

        LogOutboundRequest(HttpMethod.Get, url);

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
        return await SendAsync(HttpMethod.Delete, endpoint, null, clientHeaders);
    }

    /// <summary>
    /// Safely sends a GET request to the Jellyfin server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> GetBytesSafeAsync(
        string endpoint,
        Dictionary<string, string>? queryParams = null,
        IHeaderDictionary? clientHeaders = null)
    {
        try
        {
            var url = BuildUrl(endpoint, queryParams);
            using var request = CreateClientGetRequest(
                url, clientHeaders, out _, out _);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    _logger.LogDebug("Image not available for {Endpoint}", endpoint);
                else
                    _logger.LogWarning("Image request for {Endpoint} returned {StatusCode}", endpoint, response.StatusCode);
                return (null, null, false);
            }

            return (await response.Content.ReadAsByteArrayAsync(),
                response.Content.Headers.ContentType?.ToString(), true);
        }
        catch (Exception ex)
        {
            // Actual errors should still be logged
            _logger.LogError(ex, "Failed to get bytes from {Endpoint}", endpoint);
            return (null, null, false);
        }
    }

    /// <summary>
    /// Reads only the first bounded range of a media stream. Diagnostics use this to
    /// prove that an authenticated player can receive audio without downloading a song.
    /// </summary>
    public async Task<(int StatusCode, int BytesRead, string? ContentType, bool Success)> ProbeAudioStreamAsync(
        string itemId,
        IHeaderDictionary clientHeaders,
        int maximumBytes = 65_536,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId) || maximumBytes is < 1 or > 1_048_576)
            return (StatusCodes.Status400BadRequest, 0, null, false);

        try
        {
            var url = BuildUrl(
                $"Audio/{Uri.EscapeDataString(itemId)}/stream",
                new Dictionary<string, string> { ["static"] = "true" });
            using var request = CreateClientGetRequest(url, clientHeaders, out _, out _);
            request.Headers.Range = new RangeHeaderValue(0, maximumBytes - 1);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ((int)response.StatusCode, 0, response.Content.Headers.ContentType?.ToString(), false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[maximumBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read == 0) break;
                total += read;
            }

            return ((int)response.StatusCode, total,
                response.Content.Headers.ContentType?.ToString(), total > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authenticated audio stream probe failed");
            return (StatusCodes.Status502BadGateway, 0, null, false);
        }
    }

    /// <summary>
    /// Searches for items in Jellyfin.
    /// Does not force any library filtering - clients can specify parentId if they want.
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

        // Note: We don't force parentId here - let clients specify which library to search
        // The controller will detect music library searches and add external results

        if (includeItemTypes != null && includeItemTypes.Length > 0)
        {
            queryParams["includeItemTypes"] = string.Join(",", includeItemTypes);
        }

        var (body, statusCode) = await GetJsonAsync("Items", queryParams, clientHeaders);

        var count = 0;
        if (body != null && body.RootElement.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            count = itemsEl.GetArrayLength();
        }

        _logger.LogInformation(
            "SEARCH TRACE: JellyfinProxy.SearchAsync query='{Query}', includeItemTypes='{ItemTypes}', limit={Limit}, status={StatusCode}, returnedItems={ItemCount}",
            searchTerm,
            includeItemTypes == null ? "" : string.Join(",", includeItemTypes),
            limit,
            statusCode,
            count);

        return (body, statusCode);
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
        IHeaderDictionary? effectiveHeaders = clientHeaders;

        if (!string.IsNullOrEmpty(_settings.UserId))
        {
            queryParams["userId"] = _settings.UserId;
        }

        if ((effectiveHeaders == null || effectiveHeaders.Count == 0) &&
            !string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            effectiveHeaders = new HeaderDictionary
            {
                ["X-Emby-Token"] = _settings.ApiKey
            };
        }

        return await GetJsonAsync($"Items/{itemId}", queryParams, effectiveHeaders);
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

            LogOutboundRequest(HttpMethod.Get, url);

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
            return new ObjectResult(new { error = "Error streaming" })
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
        int? maxHeight = null,
        string? imageTag = null,
        IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>();

        if (maxWidth.HasValue)
        {
            queryParams["maxWidth"] = maxWidth.Value.ToString();
        }

        if (maxHeight.HasValue)
        {
            queryParams["maxHeight"] = maxHeight.Value.ToString();
        }

        // Jellyfin uses `tag` for image cache busting when artwork changes.
        if (!string.IsNullOrWhiteSpace(imageTag))
        {
            queryParams["tag"] = imageTag;
        }

        var execution = _httpContextAccessor.HttpContext?.GetProtocolExecutionContext();
        var actor = execution?.Actor;
        var asset = await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                actor?.TenantId,
                actor?.EffectiveUserId,
                null,
                "jellyfin",
                imageType,
                itemId,
                $"{_settings.Url}|{imageTag}",
                maxWidth,
                maxHeight),
            async _ =>
            {
                var result = await GetBytesSafeAsync(
                    $"Items/{itemId}/Images/{imageType}", queryParams, clientHeaders);
                return result.Success && result.Body != null && result.ContentType != null
                    ? new MediaAssetSource(result.Body, result.ContentType)
                    : null;
            },
            10 * 1024 * 1024,
            _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);
        return (asset?.Bytes, asset?.ContentType);
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

            var (result, statusCode) = await GetJsonAsyncInternal("Library/MediaFolders", queryParams);
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

    private void LogOutboundRequest(HttpMethod method, string url)
    {
        if (!_configuration.GetValue<bool>("Debug:LogAllRequests"))
        {
            return;
        }

        var urlForLog = MaskSensitiveUrl(url);

        _logger.LogInformation("➡️ Jellyfin {Method} {Url}", method.Method, urlForLog);
    }

    private static string MaskSensitiveUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return url;
        }

        var query = uri.Query.TrimStart('?');
        var parts = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]);
                return IsSensitiveQueryKey(key)
                    ? $"{kv[0]}=<redacted>"
                    : part;
            })
            .ToArray();

        if (parts.Length == 0)
        {
            return url;
        }

        return $"{uri.GetLeftPart(UriPartial.Path)}?{string.Join("&", parts)}{uri.Fragment}";
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        return string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "token", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "authorization", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "x-emby-token", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "x-emby-authorization", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }

    private void LogUpstreamFailure(HttpMethod method, HttpStatusCode statusCode, string url)
    {
        url = MaskSensitiveUrl(url);
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogDebug("Jellyfin {Method} returned 401 for {Url} - client should re-authenticate",
                method.Method, url);
            return;
        }

        var isLikelyBotProbe = BotProbeDetector.IsHighConfidenceProbeUrl(url);

        if (statusCode == HttpStatusCode.NotFound)
        {
            if (isLikelyBotProbe)
            {
                _logger.LogDebug("Likely bot probe returned 404 for {Url}", url);
            }
            else
            {
                _logger.LogDebug("Jellyfin {Method} returned 404 for {Url}", method.Method, url);
            }

            return;
        }

        if (isLikelyBotProbe)
        {
            _logger.LogWarning("Likely bot probe returned {StatusCode} for {Url}", statusCode, url);

            return;
        }

        _logger.LogError("Jellyfin {Method} request failed: {StatusCode} for {Url}",
            method.Method, statusCode, url);
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

        LogOutboundRequest(HttpMethod.Get, url);

        var response = await _httpClient.SendAsync(request);
        var statusCode = (int)response.StatusCode;
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LogUpstreamFailure(HttpMethod.Get, response.StatusCode, url);
            return (null, statusCode);
        }

        try
        {
            var jsonDocument = JsonDocument.Parse(content);
            return (jsonDocument, statusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Jellyfin JSON response from {Url}", MaskSensitiveUrl(url));
            return (null, statusCode);
        }
    }
}
