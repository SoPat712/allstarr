using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Handles proxying requests to the underlying Subsonic server.
/// </summary>
public class SubsonicProxyService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
	private readonly IHttpContextAccessor _httpContextAccessor;

    public SubsonicProxyService(
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Options.IOptions<SubsonicSettings> subsonicSettings,
		IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
		_httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Relays a request to the Subsonic server and returns the response.
    /// </summary>
    public async Task<(byte[] Body, string? ContentType)> RelayAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(kv => 
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_subsonicSettings.Url}/{endpoint}?{query}";
        
        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();
        
        return (body, contentType);
    }

    /// <summary>
    /// Safely relays a request to the Subsonic server, returning null on failure.
    /// </summary>
    public async Task<(byte[]? Body, string? ContentType, bool Success)> RelaySafeAsync(
        string endpoint, 
        Dictionary<string, string> parameters)
    {
        try
        {
            var result = await RelayAsync(endpoint, parameters);
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
        try
        {
			// Get HTTP context for request/response forwarding
			var httpContext = _httpContextAccessor.HttpContext;
			if (httpContext == null)
			{
				return new StatusCodeResult(500);
			}
			
			var incomingRequest = httpContext.Request;
			var outgoingResponse = httpContext.Response;

            var query = string.Join("&", parameters.Select(kv => 
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var url = $"{_subsonicSettings.Url}/rest/stream?{query}";
            			
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

			// Forward Range headers (fix for iOS client)
			if (incomingRequest.Headers.TryGetValue("Range", out var range))
			{
				request.Headers.TryAddWithoutValidation("Range", range.ToString());
			}
			
			if (incomingRequest.Headers.TryGetValue("If-Range", out var ifRange))
			{
				request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToString());
			}
			
            var response = await _httpClient.SendAsync(
                request, 
                HttpCompletionOption.ResponseHeadersRead, 
                cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return new StatusCodeResult((int)response.StatusCode);
            }

			// Iterate over and forward streaming-required headers
			foreach (var header in new[]
			{
				"Accept-Ranges",
				"Content-Range",
				"Content-Length",
				"ETag",
				"Last-Modified"
			})
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
            return new ObjectResult(new { error = $"Error streaming from Subsonic: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }
}
