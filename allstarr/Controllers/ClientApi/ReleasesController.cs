using allstarr.Core.Artwork;
using allstarr.Core.Caching;
using allstarr.Core.ClientApi;
using allstarr.Core.Releases;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/releases")]
public sealed class ReleasesController(ReleaseReader reader, ReleaseArtworkReader artwork, IApplicationCache cache) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("listReleases")]
    [ProducesResponseType<ClientReleases>(200)]
    public Task<ActionResult<ClientReleases>> List() => ExecuteAsync(() => reader.ListAsync(HttpContext, Execution));

    [HttpGet("{releaseId}/cover")]
    [EndpointName("getReleaseCover")]
    [ProducesResponseType(typeof(byte[]), 200, "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif")]
    [ProducesResponseType(204)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Cover(string releaseId)
    {
        try
        {
            var source = await reader.CoverAsync(HttpContext, Execution, releaseId);
            if (source == null) return NotFound();
            var asset = await artwork.ReadAsync(Execution.RequireActor().EffectiveUserId!.Value, Execution,
                source.ProviderId, source.AlbumId, Execution.CancellationToken, releaseId, cache);
            Response.Headers.CacheControl = asset == null ? "private, max-age=3600" : "private, max-age=604800";
            if (asset == null) return NoContent();
            var etag = ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes);
            Response.Headers.ETag = etag;
            return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)
                ? StatusCode(304) : File(asset.Bytes, asset.ContentType);
        }
        catch (TimeoutException)
        {
            Response.Headers.CacheControl = "private, max-age=60";
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action) where T : class
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await action()); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
