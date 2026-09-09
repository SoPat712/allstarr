using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace allstarr.Services.Jellyfin;

public class JellyfinProxyService
{
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

    private async Task<string?> GetMusicLibraryIdAsync()
    {
        if (!string.IsNullOrEmpty(_settings.LibraryId))
        {
            return _settings.LibraryId;
        }

        if (_libraryIdDetected)
        {
            return _cachedMusicLibraryId;
        }

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
            _libraryIdDetected = true;
            return null;
        }
    }

    public async Task<string?> GetMusicLibraryIdForFilteringAsync(
        string? callerQuery = null,
        IHeaderDictionary? clientHeaders = null)
    {
        if (!string.IsNullOrWhiteSpace(callerQuery))
        {
            var (views, _) = await GetJsonAsync($"UserViews{callerQuery}", null, clientHeaders);
            using (views)
            {
                var visibleLibraryId = FindMusicLibraryId(views);
                if (!string.IsNullOrWhiteSpace(visibleLibraryId)) return visibleLibraryId;
            }
        }

        return await GetMusicLibraryIdAsync();
    }

    private string GetAuthorizationHeader()
    {
        return $"MediaBrowser Client=\"{_settings.ClientName}\", " +
               $"Device=\"{_settings.DeviceName}\", " +
               $"DeviceId=\"{_settings.DeviceId}\", " +
               $"Version=\"{_settings.ClientVersion}\", " +
               $"Token=\"{_settings.ApiKey}\"";
    }

    public async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsync(string endpoint, Dictionary<string, string>? queryParams = null, IHeaderDictionary? clientHeaders = null)
    {
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
                : NormalizeQueryCredentials($"{BuildUrl(baseEndpoint)}?{mergedQuery}");

            return await GetJsonAsyncInternal(url, clientHeaders);
        }

        var finalUrl = BuildUrl(endpoint, queryParams);
        return await GetJsonAsyncInternal(finalUrl, clientHeaders);
    }

    // Catch-all relays must preserve arbitrary bodies and client-owned authentication.
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
            (incoming.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
             incoming.ContentLength is > 0 ||
             incoming.Headers.ContainsKey("Transfer-Encoding")))
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
        else if (MethodCanHaveBody(method))
        {
            request.Content = new ByteArrayContent([]);
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

        using var response = await _httpClient.SendAsync(request);

        var statusCode = (int)response.StatusCode;

        // Preserve JSON error bodies so clients can respond to authentication failures.
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (!isBrowserStaticRequest && !isPublicEndpoint)
            {
                LogUpstreamFailure(HttpMethod.Get, response.StatusCode, url);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    var errorDoc = JsonDocument.Parse(content);
                    return (errorDoc, statusCode);
                }
                catch
                {
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

        if (_httpContextAccessor.HttpContext != null)
        {
            var clientIp = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientIp))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp);
            }
        }

        isBrowserStaticRequest = url.Contains("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
                                 url.Contains("/web/", StringComparison.OrdinalIgnoreCase) ||
                                 (clientHeaders?.Any(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) &&
                                                         h.Value.ToString().Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) == true &&
                                  clientHeaders?.Any(h => h.Key.Equals("sec-fetch-dest", StringComparison.OrdinalIgnoreCase) &&
                                                         (h.Value.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) ||
                                                          h.Value.ToString().Contains("document", StringComparison.OrdinalIgnoreCase))) == true);

        isPublicEndpoint = url.Contains("/System/Info/Public", StringComparison.OrdinalIgnoreCase) ||
                           url.Contains("/Branding/", StringComparison.OrdinalIgnoreCase) ||
                           url.Contains("/Startup/", StringComparison.OrdinalIgnoreCase);

        var authHeaderAdded = false;

        if (clientHeaders != null && clientHeaders.Count > 0)
        {
            authHeaderAdded = AuthHeaderHelper.ForwardAuthHeaders(clientHeaders, request);

            if (authHeaderAdded)
            {
                _logger.LogTrace("Forwarded authentication headers");
            }

            // Some Jellyfin clients authenticate through the query string.
            if (!authHeaderAdded && url.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
            {
                authHeaderAdded = true;
                _logger.LogTrace("Using api_key from query string");
            }
        }

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

        using var response = await _httpClient.SendAsync(request);
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

    // Buffers the full response; callers must restrict this to bounded assets.
    public async Task<(byte[] Body, string? ContentType)> GetBytesAsync(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(endpoint, queryParams);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", GetAuthorizationHeader());

        LogOutboundRequest(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();

        return (body, contentType);
    }

    public async Task<(JsonDocument? Body, int StatusCode)> DeleteAsync(string endpoint, IHeaderDictionary clientHeaders)
    {
        return await SendAsync(HttpMethod.Delete, endpoint, null, clientHeaders);
    }

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
            _logger.LogError(ex, "Failed to get bytes from {Endpoint}", endpoint);
            return (null, null, false);
        }
    }

    // Diagnostics prove authenticated playback with a bounded range, not a full download.
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
        AddEffectiveUserId(queryParams, clientHeaders);

        // The controller, not this transparent backend query, decides when to merge providers.

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
        AddEffectiveUserId(queryParams, clientHeaders);

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

    public async Task<(JsonDocument? Body, int StatusCode)> GetItemAsync(string itemId, IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["ids"] = itemId,
            ["limit"] = "1"
        };
        IHeaderDictionary? effectiveHeaders = clientHeaders;
        var usesInternalCredential = effectiveHeaders == null ||
                                     !AuthHeaderHelper.HasAuthentication(effectiveHeaders);
        AddEffectiveUserId(queryParams, clientHeaders);

        if (usesInternalCredential && !string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            effectiveHeaders = new HeaderDictionary
            {
                ["X-Emby-Token"] = _settings.ApiKey
            };
        }

        var (body, statusCode) = await GetJsonAsync("Items", queryParams, effectiveHeaders);
        using (body)
        {
            if (statusCode != StatusCodes.Status200OK || body == null ||
                !body.RootElement.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
                return (null, statusCode);

            return (JsonDocument.Parse(items[0].GetRawText()), statusCode);
        }
    }

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
        AddEffectiveUserId(queryParams, clientHeaders);

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

    public async Task<(JsonDocument? Body, int StatusCode)> GetArtistAsync(string artistIdOrName, IHeaderDictionary? clientHeaders = null)
    {
        var queryParams = new Dictionary<string, string>();
        AddEffectiveUserId(queryParams, clientHeaders);

        if (Guid.TryParse(artistIdOrName, out _))
        {
            return await GetJsonAsync($"Items/{artistIdOrName}", queryParams, clientHeaders);
        }

        return await GetJsonAsync($"Artists/{Uri.EscapeDataString(artistIdOrName)}", queryParams, clientHeaders);
    }

    private void AddEffectiveUserId(
        IDictionary<string, string> queryParams,
        IHeaderDictionary? clientHeaders)
    {
        var clientUserId = clientHeaders == null
            ? null
            : AuthHeaderHelper.ExtractUserId(clientHeaders);
        if (!string.IsNullOrWhiteSpace(clientUserId))
        {
            queryParams["userId"] = clientUserId;
        }
        else if (!string.IsNullOrEmpty(_settings.UserId) &&
                 (clientHeaders == null || !AuthHeaderHelper.HasAuthentication(clientHeaders)))
        {
            queryParams["userId"] = _settings.UserId;
        }
    }

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

            var queryParams = new Dictionary<string, string>
            {
                ["static"] = "true",
                ["mediaSourceId"] = itemId
            };

            var url = BuildUrl($"Audio/{itemId}/stream", queryParams);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", GetAuthorizationHeader());

            LogOutboundRequest(HttpMethod.Get, url);

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

            outgoingResponse.StatusCode = (int)response.StatusCode;

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

            using (result) return FindMusicLibraryId(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get music library ID");
            return null;
        }
    }

    private static string? FindMusicLibraryId(JsonDocument? document)
    {
        if (document == null ||
            !document.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("CollectionType", out var collectionType) &&
                string.Equals(collectionType.GetString(), "music", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("Id", out var id))
                return id.GetString();
        }

        return null;
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

        return NormalizeQueryCredentials(url);
    }

    internal static string NormalizeQueryCredentials(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return url;

        var fragmentStart = url.IndexOf('#', queryStart);
        var queryEnd = fragmentStart >= 0 ? fragmentStart : url.Length;
        var query = url[(queryStart + 1)..queryEnd];
        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var selectedCredentialIndex = Array.FindIndex(parameters, parameter =>
            Uri.UnescapeDataString(parameter.Split('=', 2)[0]).Equals("ApiKey", StringComparison.Ordinal));
        if (selectedCredentialIndex < 0)
        {
            selectedCredentialIndex = Array.FindIndex(parameters, parameter =>
            {
                var key = Uri.UnescapeDataString(parameter.Split('=', 2)[0]);
                return key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("access_token", StringComparison.OrdinalIgnoreCase);
            });
        }
        var preserved = new List<string>(parameters.Length);

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var keyValue = parameter.Split('=', 2);
            var key = Uri.UnescapeDataString(keyValue[0]);
            if (key.Equals("ApiKey", StringComparison.Ordinal) ||
                key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            {
                if (index == selectedCredentialIndex)
                {
                    preserved.Add($"ApiKey={(keyValue.Length == 2 ? keyValue[1] : string.Empty)}");
                }
                continue;
            }

            preserved.Add(parameter);
        }

        var normalizedQuery = string.Join('&', preserved);
        var fragment = fragmentStart >= 0 ? url[fragmentStart..] : string.Empty;
        return normalizedQuery.Length == 0
            ? $"{url[..queryStart]}{fragment}"
            : $"{url[..queryStart]}?{normalizedQuery}{fragment}";
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
        return string.Equals(key, "ApiKey", StringComparison.Ordinal) ||
               string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase) ||
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

    // The server API key is restricted to internal operations; client proxying retains client auth.
    public async Task<(JsonDocument? Body, int StatusCode)> GetJsonAsyncInternal(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl(endpoint, queryParams);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var authHeader = GetAuthorizationHeader();
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        LogOutboundRequest(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request);
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
