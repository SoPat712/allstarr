using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [AcceptVerbs("GET", "POST", "HEAD")]
    [Route("rest/download")]
    [Route("rest/download.view")]
    public async Task<IActionResult> Download()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");
        if (string.IsNullOrWhiteSpace(id))
            return DownloadError(format, 10, "Missing id parameter", StatusCodes.Status400BadRequest);

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);
        if (isExternal && _libraryIndex != null &&
            await _libraryIndex.ResolveKnownTrackAsync(CurrentProtocolContext, id, HttpContext.RequestAborted) is { } nativeId)
        {
            id = nativeId;
            parameters = parameters.SetValue("id", nativeId);
            isExternal = false;
        }
        if (!isExternal)
        {
            var endpoint = Request.Path.Value?.TrimStart('/') ?? "rest/download";
            var relayed = await _proxyService.RelayRawAsync(endpoint, parameters, HttpContext.RequestAborted, Request.Headers);
            return _relayProtocolAdapter.CreateResult(relayed, $"application/{format}");
        }

        try
        {
            var result = await _streamOpener.OpenAsync(HttpContext,
                CurrentProtocolContext with { RequestedItemId = id, IsOriginalDownload = true },
                provider!, externalId!, ProviderAudioQuality.Any);
            if (result is IStatusCodeActionResult { StatusCode: >= 400 } failure)
            {
                var error = SubsonicExceptionFilter.Map(new HttpRequestException(null, null, (HttpStatusCode)failure.StatusCode.Value));
                return DownloadError(format, error.Code, error.Message, failure.StatusCode.Value);
            }
            return result;
        }
        catch (Exception exception)
        {
            var error = SubsonicExceptionFilter.Map(exception, HttpContext.RequestAborted.IsCancellationRequested);
            return DownloadError(format, error.Code, error.Message, error.StatusCode);
        }
    }

    private IActionResult DownloadError(string format, int code, string message, int statusCode)
    {
        var result = _responseBuilder.CreateError(format, code, message);
        if (result is JsonResult json) json.StatusCode = statusCode;
        if (result is ContentResult content) content.StatusCode = statusCode;
        return result;
    }
}
