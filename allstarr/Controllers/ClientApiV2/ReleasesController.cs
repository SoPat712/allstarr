using allstarr.Core.Artwork;
using allstarr.Core.Caching;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Releases;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/releases")]
public sealed class ReleasesController(V2LibraryEndpoints library, ReleaseReader reader, ReleaseArtworkReader artwork, IApplicationCache cache) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("listV2Releases")]
    [EndpointDescription("Lists stored releases and followed artists for this listener, with native album and artist ids and cover URLs. Also available in batch.")]
    [ProducesResponseType<V2Releases>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> List() => Respond(await library.ReleasesAsync(HttpContext, Execution));

    [HttpGet("{id}/cover")]
    [EndpointName("getV2ReleaseCover")]
    [EndpointDescription("Serves a release's stored artwork after checking the listener's release inbox. Hidden releases return 404; absent artwork returns 204.")]
    [ProducesResponseType(typeof(byte[]), 200, "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif")]
    [ProducesResponseType(204)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Cover(string id)
    {
        if (!V2LibraryEndpoints.IsReleaseId(id)) return Respond(new(400, null));
        try
        {
            var source = await reader.CoverAsync(HttpContext, Execution, id);
            if (source == null) return Respond(new(404, null));
            var asset = await artwork.ReadAsync(Execution.RequireActor().EffectiveUserId!.Value, Execution,
                source.ProviderId, source.AlbumId, Execution.CancellationToken, id, cache);
            Response.Headers.CacheControl = asset == null ? "private, max-age=3600" : "private, max-age=604800";
            if (asset == null) return NoContent();
            var etag = ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes);
            Response.Headers.ETag = etag;
            return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag) ? StatusCode(304) : File(asset.Bytes, asset.ContentType);
        }
        catch (TimeoutException)
        {
            Response.Headers.CacheControl = "private, max-age=60";
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Respond(new(404, null)); }
        catch (KeyNotFoundException) { return Respond(new(404, null)); }
        catch (ArgumentException) { return Respond(new(400, null)); }
        catch (HttpRequestException) { return Respond(new(503, null)); }
    }
}
