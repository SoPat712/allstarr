using System.ComponentModel;
using allstarr.Core.Artwork;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Music;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/mixes")]
public sealed class MixesController(V2LibraryEndpoints library, MixReader reader, MixCoverImageService covers) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("listV2Mixes")]
    [EndpointDescription("Lists this listener's mixes, with cover URLs, app publication state and playlist ids. Also available in batch.")]
    [ProducesResponseType<V2Mixes>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> List([FromQuery, Description("Time zone for dated mixes; defaults to the listener's stored zone.")] string? timeZoneId = null) =>
        Respond(await library.MixesAsync(HttpContext, Execution, timeZoneId));

    [HttpGet("{id}/tracks")]
    [EndpointName("listV2MixTracks")]
    [EndpointDescription("Reads a mix's tracks in stored order. Hidden mixes return 404. Accepts a v2 mix id or its older UUID. Also available in batch.")]
    [ProducesResponseType<V2MixTracks>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Tracks(string id) => Respond(await library.MixTracksAsync(HttpContext, Execution, id));

    [HttpGet("{id}/cover")]
    [EndpointName("getV2MixCover")]
    [EndpointDescription("Serves an authorized mix's cover. The version in the mix's cover URL enables immutable caching.")]
    [ProducesResponseType(typeof(byte[]), 200, "image/jpeg", "image/png")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Cover(string id, [FromQuery(Name = "v"), Description("Cover version from the mix's cover URL.")] string? version = null)
    {
        if (!V2LibraryEndpoints.TryMixId(id, out var mix)) return Respond(new(400, null));
        if (version != null && (version.Length != 16 || !version.All(char.IsAsciiHexDigit))) return Respond(new(400, null));
        try
        {
            await reader.EnsureCoverAccessAsync(Execution, mix);
            var owner = Execution.RequireActor().EffectiveUserId!.Value;
            if (version != null && await covers.TryCreateCachedResponseAsync(mix, owner, version, HttpContext) is { } cached) return WithValidator(cached);
            return WithValidator(await covers.CreateResponseAsync(await reader.CoverAsync(Execution, mix), HttpContext, version, owner));
        }
        catch (UnauthorizedAccessException) { return Respond(new(404, null)); }
        catch (KeyNotFoundException) { return Respond(new(404, null)); }
        catch (ArgumentException) { return Respond(new(400, null)); }
        catch (HttpRequestException) { return Respond(new(503, null)); }
    }

    private IActionResult WithValidator(IActionResult result)
    {
        if (result is not FileContentResult file || Response.Headers.ETag.Count > 0) return result;
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(file.FileContents);
        Response.Headers.ETag = etag;
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag) ? StatusCode(304) : result;
    }
}
