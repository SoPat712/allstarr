using Microsoft.AspNetCore.Mvc;
using allstarr.Models.Settings;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using allstarr.Core.Protocols;

namespace allstarr.Services.Subsonic;

/// <summary>
/// Handles proxying requests to the underlying Subsonic server.
/// </summary>
public class SubsonicProxyService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ProtocolStreamingResponseAdapter _streamingResponseAdapter;

    public SubsonicProxyService(
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Options.IOptions<SubsonicSettings> subsonicSettings,
        IHttpContextAccessor httpContextAccessor,
        ProtocolStreamingResponseAdapter? streamingResponseAdapter = null)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _httpContextAccessor = httpContextAccessor;
        _streamingResponseAdapter = streamingResponseAdapter ?? new ProtocolStreamingResponseAdapter();
    }

    /// <summary>
    /// Relays a request to the Subsonic server and returns the response.
    /// </summary>
    public async Task<(byte[] Body, string? ContentType)> RelayAsync(
        string endpoint,
        Dictionary<string, string> parameters)
    {
        return await RelayAsync(endpoint, SubsonicRequestParameters.FromDictionary(parameters));
    }

    public async Task<(byte[] Body, string? ContentType)> RelayAsync(
        string endpoint,
        SubsonicRequestParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var response = await RelayRawAsync(endpoint, parameters, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Subsonic returned {(int)response.StatusCode}",
                inner: null,
                response.StatusCode);
        }

        // Trigger GC for large files to prevent memory leaks
        if (response.Body.Length > 1024 * 1024) // 1MB threshold
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }

        return (response.Body, response.ContentType);
    }

    public async Task<SubsonicProxyResponse> RelayRawAsync(
        string endpoint,
        SubsonicRequestParameters parameters,
        CancellationToken cancellationToken = default,
        IHeaderDictionary? requestHeaders = null)
    {
        using var request = CreateRelayRequest(endpoint, parameters);
        ForwardConditionalRequestHeaders(requestHeaders, request);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(header => header.Value).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new SubsonicProxyResponse(
            body,
            response.Content.Headers.ContentType?.ToString(),
            response.StatusCode,
            headers);
    }

    /// <summary>
    /// Safely relays a request to the Subsonic server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> RelaySafeAsync(
        string endpoint,
        Dictionary<string, string> parameters)
    {
        return await RelaySafeAsync(endpoint, SubsonicRequestParameters.FromDictionary(parameters));
    }

    public async Task<(byte[]? Body, string? ContentType, bool Success)> RelaySafeAsync(
        string endpoint,
        SubsonicRequestParameters parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RelayAsync(endpoint, parameters, cancellationToken);
            return (result.Body, result.ContentType, true);
        }
        catch
        {
            return (null, null, false);
        }
    }

    /// <summary>
    /// Relays a stream request to the Subsonic server with range processing support.
    /// </summary>
    public async Task<IActionResult> RelayStreamAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        return await RelayStreamAsync(
            SubsonicRequestParameters.FromDictionary(parameters),
            cancellationToken);
    }

    public async Task<IActionResult> RelayStreamAsync(
        SubsonicRequestParameters parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get HTTP context for request/response forwarding
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return new ObjectResult(new { error = "HTTP context not available" })
                {
                    StatusCode = 500
                };
            }

            var incomingRequest = httpContext.Request;
            using var request = CreateRelayRequest("rest/stream", parameters);
            _streamingResponseAdapter.ForwardRangeRequestHeaders(incomingRequest.Headers, request);

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return await _streamingResponseAdapter.CreateAsync(
                httpContext,
                response,
                cancellationToken,
                enableRangeProcessing: false);
        }
        catch (Exception ex)
        {
            return ProtocolStreamingResponseAdapter.CreateTransportFailure(
                cancellationToken,
                ex,
                "Error streaming from Subsonic");
        }
    }

    private HttpRequestMessage CreateRelayRequest(
        string endpoint,
        SubsonicRequestParameters parameters)
    {
        var query = SubsonicRequestParameters.EncodePairs(parameters.QueryParameters);
        var backendUrl = _subsonicSettings.Url ??
                         throw new InvalidOperationException("Subsonic backend URL is not configured");
        var url = $"{backendUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
        if (!string.IsNullOrEmpty(query))
        {
            url = $"{url}?{query}";
        }

        var method = new HttpMethod(parameters.Method);
        var request = new HttpRequestMessage(method, url);
        if (method != HttpMethod.Get &&
            method != HttpMethod.Head &&
            parameters.RawBody != null)
        {
            request.Content = new StringContent(parameters.RawBody, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(parameters.ContentType))
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(parameters.ContentType);
            }
        }

        return request;
    }

    private static void ForwardConditionalRequestHeaders(
        IHeaderDictionary? incoming,
        HttpRequestMessage request)
    {
        if (incoming == null)
        {
            return;
        }

        foreach (var name in new[]
                 {
                     "Accept",
                     "Accept-Language",
                     "If-Match",
                     "If-None-Match",
                     "If-Modified-Since",
                     "If-Unmodified-Since"
                 })
        {
            if (incoming.TryGetValue(name, out var values))
            {
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }
    }
}

public sealed record SubsonicProxyResponse(
    byte[] Body,
    string? ContentType,
    HttpStatusCode StatusCode,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}
