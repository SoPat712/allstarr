using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Audio Streaming

    /// <summary>
    /// Downloads/streams audio. Works with local and external content.
    /// </summary>
    [HttpGet("Items/{itemId}/Download")]
    [HttpGet("Items/{itemId}/File")]
    public async Task<IActionResult> DownloadAudio(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // Build path for Jellyfin download/file endpoint
            var endpoint = Request.Path.Value?.Contains("/File", StringComparison.OrdinalIgnoreCase) == true
                ? "File"
                : "Download";
            var fullPath = $"Items/{itemId}/{endpoint}";
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // Handle external content
        return await StreamExternalContent(provider!, externalId!);
    }

    /// <summary>
    /// Streams audio for a given item. Downloads on-demand for external content.
    /// </summary>
    [HttpGet("Audio/{itemId}/stream")]
    [HttpGet("Audio/{itemId}/stream.{container}")]
    public async Task<IActionResult> StreamAudio(string itemId, string? container = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // Build path for Jellyfin stream
            var fullPath = string.IsNullOrEmpty(container)
                ? $"Audio/{itemId}/stream"
                : $"Audio/{itemId}/stream.{container}";

            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // Handle external content with quality override from client transcoding params
        var quality = StreamQualityHelper.ParseFromQueryString(Request.Query);
        return await StreamExternalContent(provider!, externalId!, quality);
    }

    /// <summary>
    /// Proxies a stream from Jellyfin with proper header forwarding.
    /// </summary>
    private async Task<IActionResult> ProxyJellyfinStream(string path, string itemId)
    {
        var jellyfinUrl = $"{_settings.Url?.TrimEnd('/')}/{path}";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, jellyfinUrl);

            // Forward auth headers
            AuthHeaderHelper.ForwardAuthHeaders(Request.Headers, request);

            // Forward Range header for seeking
            if (Request.Headers.TryGetValue("Range", out var range))
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToString());
            }

            var response = await _proxyService.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jellyfin stream failed: {StatusCode} for {ItemId}", response.StatusCode, itemId);
                return StatusCode((int)response.StatusCode);
            }

            // Set response status and headers
            Response.StatusCode = (int)response.StatusCode;

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

            // Forward caching headers for client-side caching
            if (response.Headers.ETag != null)
            {
                Response.Headers["ETag"] = response.Headers.ETag.ToString();
            }

            if (response.Content.Headers.LastModified.HasValue)
            {
                Response.Headers["Last-Modified"] = response.Content.Headers.LastModified.Value.ToString("R");
            }

            if (response.Headers.CacheControl != null)
            {
                Response.Headers["Cache-Control"] = response.Headers.CacheControl.ToString();
            }

            // Forward range headers for seeking
            if (response.Content.Headers.ContentRange != null)
            {
                Response.Headers["Content-Range"] = response.Content.Headers.ContentRange.ToString();
            }

            if (response.Headers.AcceptRanges != null)
            {
                Response.Headers["Accept-Ranges"] = string.Join(", ", response.Headers.AcceptRanges);
            }

            if (response.Content.Headers.ContentLength.HasValue)
            {
                Response.Headers["Content-Length"] = response.Content.Headers.ContentLength.Value.ToString();
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return File(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy stream from Jellyfin for {ItemId}", itemId);
            return StatusCode(500, new { error = "Streaming failed" });
        }
    }

    /// <summary>
    /// Streams external content, using cache if available or downloading on-demand.
    /// Supports quality override for client-requested "transcoding" of external tracks.
    /// </summary>
    private async Task<IActionResult> StreamExternalContent(string provider, string externalId, StreamQuality quality = StreamQuality.Original)
    {
        // Check for locally cached file
        var localPath = await _localLibraryService.GetLocalPathForExternalSongAsync(provider, externalId);

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            // Update last write time for cache cleanup (extends cache lifetime)
            try
            {
                System.IO.File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update last write time for {Path}", localPath);
            }

            var stream = System.IO.File.OpenRead(localPath);
            return File(stream, GetContentType(localPath), enableRangeProcessing: true);
        }

        // Download and stream on-demand
        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(
                provider,
                externalId,
                quality != StreamQuality.Original ? quality : null,
                HttpContext.RequestAborted);

            var contentType = "audio/mpeg";
            if (downloadStream is FileStream fs)
            {
                contentType = GetContentType(fs.Name);
            }

            return File(downloadStream, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return HandleExternalStreamFailure(provider, externalId, ex);
        }
    }

    private IActionResult HandleExternalStreamFailure(string provider, string externalId, Exception ex)
    {
        if (HttpContext.RequestAborted.IsCancellationRequested && ex is OperationCanceledException)
        {
            _logger.LogInformation("Client aborted external stream request for {Provider}:{ExternalId}", provider, externalId);
            return StatusCode(499);
        }

        var (statusCode, errorMessage) = MapExternalStreamException(ex);

        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
        {
            _logger.LogError("Failed to stream external song {Provider}:{ExternalId}: responding {StatusCode}; upstream returned {UpstreamStatus}: {ReasonPhrase}",
                provider,
                externalId,
                statusCode,
                (int)httpRequestException.StatusCode.Value,
                httpRequestException.StatusCode.Value);
            _logger.LogDebug(ex, "Detailed streaming failure for external song {Provider}:{ExternalId}", provider, externalId);
        }
        else
        {
            _logger.LogError(ex, "Failed to stream external song {Provider}:{ExternalId}: responding {StatusCode}",
                provider, externalId, statusCode);
        }

        return StatusCode(statusCode, new { error = errorMessage });
    }

    private static (int statusCode, string errorMessage) MapExternalStreamException(Exception ex)
    {
        if (ex is TimeoutException || ex is TaskCanceledException)
        {
            return (StatusCodes.Status504GatewayTimeout, "External provider timed out");
        }

        if (ex is HttpRequestException httpRequestException)
        {
            return httpRequestException.StatusCode switch
            {
                HttpStatusCode.NotFound => (StatusCodes.Status404NotFound, "External track not found"),
                HttpStatusCode.TooManyRequests => (StatusCodes.Status503ServiceUnavailable, "External provider is rate limiting requests"),
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout or
                HttpStatusCode.InternalServerError => (StatusCodes.Status503ServiceUnavailable, "External provider is unavailable"),
                _ => (StatusCodes.Status502BadGateway, "External provider request failed")
            };
        }

        if (ex is InvalidOperationException invalidOperationException &&
            invalidOperationException.Message.Contains("endpoints", StringComparison.OrdinalIgnoreCase))
        {
            return (StatusCodes.Status503ServiceUnavailable, "External provider has no healthy endpoints");
        }

        if (ex.Message.Contains("endpoints failed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("No SquidWTF endpoints", StringComparison.OrdinalIgnoreCase))
        {
            return (StatusCodes.Status503ServiceUnavailable, "External provider has no healthy endpoints");
        }

        return (StatusCodes.Status502BadGateway, "External stream failed");
    }

    /// <summary>
    /// Universal audio endpoint - handles transcoding, format negotiation, and adaptive streaming.
    /// This is the primary endpoint used by Jellyfin Web and most clients.
    /// </summary>
    [HttpGet("Audio/{itemId}/universal")]
    [HttpHead("Audio/{itemId}/universal")]
    public async Task<IActionResult> UniversalAudio(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // For local content, proxy the universal endpoint with all query parameters
            var fullPath = $"Audio/{itemId}/universal";
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // For external content, parse quality override from client transcoding params
        var quality = StreamQualityHelper.ParseFromQueryString(Request.Query);
        return await StreamExternalContent(provider!, externalId!, quality);
    }

    #endregion
}
