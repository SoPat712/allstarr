using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/providers/{providerId}/icon")]
public sealed class ProviderIconsController : ClientApiControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(byte[]), 200, "image/svg+xml", "image/png", "image/jpeg", "image/webp", "image/x-icon")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string providerId, CancellationToken cancellationToken)
    {
        var icon = await ProviderArtworkResolver.ResolveIconAsync(HttpContext.RequestServices,
            providerId, cancellationToken);
        if (icon == null) return NotFound();
        await using var source = icon.File.CreateReadStream();
        using var output = new MemoryStream();
        await source.CopyToAsync(output, cancellationToken);
        var bytes = output.ToArray();
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(bytes);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=86400";
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)
            ? StatusCode(StatusCodes.Status304NotModified)
            : File(bytes, icon.ContentType);
    }
}
