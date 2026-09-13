using allstarr.Services.Common;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Services.Jellyfin;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Audio Streaming

    [HttpGet("Items/{itemId}/Download")]
    [HttpGet("Items/{itemId}/File")]
    [HttpHead("Items/{itemId}/Download")]
    [HttpHead("Items/{itemId}/File")]
    public async Task<IActionResult> DownloadAudio(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
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

        return await StreamExternalContent(provider!, externalId!, asDownload: true);
    }

    [HttpGet("Audio/{itemId}/stream")]
    [HttpGet("Audio/{itemId}/stream.{container}")]
    [HttpHead("Audio/{itemId}/stream")]
    [HttpHead("Audio/{itemId}/stream.{container}")]
    public async Task<IActionResult> StreamAudio(string itemId, string? container = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            var fullPath = string.IsNullOrEmpty(container)
                ? $"Audio/{itemId}/stream"
                : $"Audio/{itemId}/stream.{container}";

            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        var quality = StreamQualityHelper.ParseFromQueryString(Request.Query);
        return await StreamExternalContent(provider!, externalId!, quality);
    }

    private async Task<IActionResult> ProxyJellyfinStream(string path, string itemId)
    {
        var jellyfinUrl = JellyfinProxyService.NormalizeQueryCredentials(
            $"{_settings.Url?.TrimEnd('/')}/{path}");

        try
        {
            var request = new HttpRequestMessage(
                HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                jellyfinUrl);

            AuthHeaderHelper.ForwardAuthHeaders(Request.Headers, request);

            _streamingResponseAdapter.ForwardRangeRequestHeaders(Request.Headers, request);

            var response = await _proxyService.HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jellyfin stream failed: {StatusCode} for {ItemId}", response.StatusCode, itemId);
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                return StatusCode(statusCode);
            }
            return await _streamingResponseAdapter.CreateAsync(
                HttpContext,
                response,
                HttpContext.RequestAborted,
                enableRangeProcessing: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy stream from Jellyfin for {ItemId}", itemId);
            return ProtocolStreamingResponseAdapter.CreateTransportFailure(
                HttpContext.RequestAborted,
                ex,
                "Streaming failed");
        }
    }

    private async Task<IActionResult> StreamExternalContent(
        string provider,
        string externalId,
        StreamQuality quality = StreamQuality.Original,
        bool asDownload = false)
    {
        if (_providerGateway != null)
        {
            try
            {
                var protocol = HttpContext.RequireProtocolExecutionContext();
                var requestedQuality = quality switch
                {
                    StreamQuality.Low => ProviderAudioQuality.DataSaver,
                    StreamQuality.High => ProviderAudioQuality.Lossy,
                    _ => ProviderAudioQuality.Any
                };
                var routed = await _providerGateway.OpenStreamAsync(
                    protocol,
                    provider,
                    externalId,
                    requestedQuality,
                    Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
                    headOnly: HttpMethods.IsHead(Request.Method));
                if (routed != null)
                {
                    if (!routed.Response.IsSuccessStatusCode)
                    {
                        var status = (int)routed.Response.StatusCode;
                        routed.Response.Dispose();
                        return StatusCode(status);
                    }
                    if (_managedTrackCache != null)
                    {
                        await _managedTrackCache.WrapAsync(
                            routed,
                            routed.ServingProviderId,
                            routed.ServingExternalId ?? externalId,
                            requestedQuality,
                            HttpMethods.IsHead(Request.Method),
                            () => _providerGateway.GetSongAsync(protocol, routed.ServingProviderId,
                                routed.ServingExternalId ?? externalId),
                            HttpContext.RequestAborted);
                    }
                    return await _streamingResponseAdapter.CreateAsync(
                        HttpContext,
                        routed.Response,
                        HttpContext.RequestAborted,
                        enableRangeProcessing: routed.IsCached);
                }
                if (protocol.Actor != null) return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "No verified playback source is available." });
            }
            catch (Exception ex)
            {
                return HandleExternalStreamFailure(provider, externalId, ex);
            }
        }

        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(
                provider,
                externalId,
                quality != StreamQuality.Original ? quality : null,
                HttpContext.RequestAborted);

            var contentType = downloadStream is ProgressiveCachingStream typed
                ? typed.ContentType
                : "audio/mpeg";
            if (downloadStream is FileStream fs)
            {
                contentType = GetContentType(fs.Name);
            }

            return asDownload
                ? File(downloadStream, contentType, $"track{MediaFileExtension(contentType)}",
                    enableRangeProcessing: downloadStream.CanSeek)
                : File(downloadStream, contentType, enableRangeProcessing: downloadStream.CanSeek);
        }
        catch (Exception ex)
        {
            return HandleExternalStreamFailure(provider, externalId, ex);
        }
    }

    private static string MediaFileExtension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
        "audio/aac" => ".aac",
        _ => ".mp3"
    };

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

    internal static (int statusCode, string errorMessage) MapExternalStreamException(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return (StatusCodes.Status403Forbidden, "External provider account is unavailable");
        }

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

        if (ex.Message.Contains("endpoints failed", StringComparison.OrdinalIgnoreCase))
        {
            return (StatusCodes.Status503ServiceUnavailable, "External provider has no healthy endpoints");
        }

        return (StatusCodes.Status502BadGateway, "External stream failed");
    }

    // Jellyfin Web and most clients negotiate adaptive playback through this route.
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
            var fullPath = $"Audio/{itemId}/universal";
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        var quality = StreamQualityHelper.ParseFromQueryString(Request.Query);
        return await StreamExternalContent(provider!, externalId!, quality);
    }

    #endregion
}
