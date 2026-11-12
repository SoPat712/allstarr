using allstarr.Core.Artwork;
using allstarr.Core.Capabilities;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/providers")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ProviderArtworkController(IMediaAssetResolver assets) : ControllerBase
{
    [HttpGet("{provider}/cover")]
    public async Task<IActionResult> Cover(string provider, [FromQuery] int? size = null)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var session) || session is not AdminAuthSession)
            return Unauthorized();
        try { provider = ProviderContractValidation.ProviderId(provider, nameof(provider)); }
        catch (ArgumentException) { return NotFound(); }
        var dimension = size is > 0 and <= 96 ? 96 : 600;
        var asset = await ProviderArtworkResolver.ResolveCoverAsync(HttpContext.RequestServices,
            assets, provider, dimension, HttpContext.RequestAborted);
        if (asset == null) return NotFound();
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=86400";
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)
            ? StatusCode(StatusCodes.Status304NotModified) : File(asset.Bytes, asset.ContentType);
    }
}
