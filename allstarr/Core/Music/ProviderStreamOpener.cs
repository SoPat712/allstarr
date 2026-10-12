using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Music;

/// <summary>
/// Opens a provider song for a protocol stream request: the routed source is opened once,
/// complete responses may be kept in the managed cache, and the response keeps the client's
/// range and HEAD semantics. Failures propagate so each protocol can describe them its own way.
/// </summary>
public sealed class ProviderStreamOpener(
    IProtocolProviderGateway providers,
    ProtocolStreamingResponseAdapter responses,
    ManagedTrackCacheService? cache = null)
{
    public async Task<IActionResult> OpenAsync(
        HttpContext http,
        ProtocolExecutionContext context,
        string providerId,
        string externalId,
        ProviderAudioQuality quality)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(context);
        var headOnly = HttpMethods.IsHead(http.Request.Method);
        var routed = await providers.OpenStreamAsync(
            context,
            providerId,
            externalId,
            quality,
            http.Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
            headOnly);
        if (routed == null)
        {
            return new ObjectResult(new { error = "No verified playback source is available." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
        if (!routed.Response.IsSuccessStatusCode)
        {
            var status = (int)routed.Response.StatusCode;
            routed.Response.Dispose();
            return new StatusCodeResult(status);
        }

        var servingId = routed.ServingExternalId ?? externalId;
        if (cache != null)
        {
            await cache.WrapAsync(
                routed,
                context,
                routed.ServingProviderId,
                servingId,
                headOnly,
                () => providers.GetSongAsync(context, routed.ServingProviderId, servingId),
                http.RequestAborted);
        }
        return await responses.CreateAsync(
            http,
            routed.Response,
            http.RequestAborted,
            enableRangeProcessing: routed.IsCached);
    }
}
