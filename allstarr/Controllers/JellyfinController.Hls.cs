using System.Globalization;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    [HttpGet("Audio/{itemId}/main.m3u8")]
    [HttpHead("Audio/{itemId}/main.m3u8")]
    public async Task<IActionResult> HlsAudio(string itemId, [FromServices] ProviderHlsSessions sessions)
    {
        var (external, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        if (external && await ResolveKnownNativeTrackAsync(itemId) is { } nativeId)
        {
            itemId = nativeId;
            external = false;
        }
        if (!external)
            return await ProxyJellyfinStream($"Audio/{itemId}/main.m3u8{Request.QueryString}", itemId);

        try
        {
            var delivery = ProviderAudioDeliveryRequests.Jellyfin(Request.Query) with { RequireHls = true };
            var start = delivery.StartTimeTicks / (double)TimeSpan.TicksPerSecond;
            var container = Request.Query["segmentContainer"].ToString();
            if (container.Length > 0 && !container.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                throw new ProviderAudioProfileException("Only AAC fragmented MP4 HLS segments are supported.");
            var session = await sessions.CreateAsync(HttpContext.RequireProtocolExecutionContext(), itemId,
                provider!, externalId!, delivery with { StartTimeTicks = 0 }, start);
            ApplyHlsMediaHeaders(session);
            var credentials = QueryString.Create(Request.Query
                .Where(pair => pair.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase)));
            var prefix = $"hls/{session.Id}/";
            var manifest = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
                $"#EXT-X-TARGETDURATION:{Math.Ceiling(session.Duration).ToString(CultureInfo.InvariantCulture)}\n" +
                "#EXT-X-MEDIA-SEQUENCE:0\n" +
                (start > 0 ? $"#EXT-X-START:TIME-OFFSET={start.ToString("0.#######", CultureInfo.InvariantCulture)},PRECISE=YES\n" : "") +
                $"#EXT-X-MAP:URI=\"{prefix}init.mp4{credentials}\"\n" +
                $"#EXTINF:{session.Duration.ToString("0.######", CultureInfo.InvariantCulture)},\n" +
                $"{prefix}0.mp4{credentials}\n#EXT-X-ENDLIST\n";
            Response.Headers.CacheControl = "private, no-store";
            return Content(manifest, "application/vnd.apple.mpegurl");
        }
        catch (Exception error)
        {
            return HandleExternalStreamFailure(provider!, externalId!, error);
        }
    }

    [HttpGet("Audio/{itemId}/hls/{sessionId}/{part}")]
    [HttpHead("Audio/{itemId}/hls/{sessionId}/{part}")]
    public async Task<IActionResult> HlsAudioSegment(string itemId, string sessionId, string part, [FromServices] ProviderHlsSessions sessions,
        [FromServices] ProviderStreamAdmission admission, [FromServices] ProtocolStreamingResponseAdapter responses)
    {
        if (part is not ("init.mp4" or "0.mp4")) return NotFound();
        var session = sessions.Find(sessionId);
        if (session == null) return StatusCode(StatusCodes.Status410Gone);
        var context = HttpContext.RequireProtocolExecutionContext();
        if (session.ItemId != itemId || session.BackendInstanceId != context.BackendInstanceId ||
            session.PrincipalId != context.VerifiedBackendPrincipalId) return NotFound();
        ApplyHlsMediaHeaders(session);
        var initialization = part == "init.mp4";
        Response.Headers.CacheControl = "private, no-store";
        var capacity = admission.TryEnter(context);
        if (capacity == null) return HandleExternalStreamFailure("allstarr", itemId, new LocalCapacityException("provider-stream"));
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(session.Bytes, initialization ? 0 : session.InitializationLength,
                initialization ? session.InitializationLength : session.Bytes.Length - session.InitializationLength, writable: false))
        };
        response.Content.Headers.ContentType = new("audio/mp4");
        response.Content.Headers.ContentLength = initialization ? session.InitializationLength : session.Bytes.Length - session.InitializationLength;
        try { return await responses.CreateAsync(HttpContext, response, HttpContext.RequestAborted, true, responseLease: capacity); }
        catch
        {
            response.Dispose();
            capacity.Dispose();
            throw;
        }
    }

    private void ApplyHlsMediaHeaders(ProviderHlsSession session)
    {
        if (session.Media is not { } media) return;
        Response.Headers["X-Allstarr-Audio-Codec"] = media.Codec;
        Response.Headers["X-Allstarr-Audio-Container"] = media.Container;
        if (media.Bitrate is { } bitrate) Response.Headers["X-Allstarr-Audio-Bitrate"] = bitrate.ToString(CultureInfo.InvariantCulture);
    }
}
