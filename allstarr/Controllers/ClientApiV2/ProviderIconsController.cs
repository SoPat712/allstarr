using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.Artwork;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/providers/{id}/icon")]
public sealed class ProviderIconsController : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("getV2ProviderIcon")]
    [EndpointDescription("Serves a provider's color or glyph icon from the installed artwork, with the same SVG and PNG choices as other clients.")]
    [ProducesResponseType(typeof(byte[]), 200, "image/svg+xml", "image/png")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string id,
        [FromQuery, AllowedValues("color", "glyph"), Description("Color logo or monochrome glyph.")] string variant = "color",
        [FromQuery, AllowedValues("svg", "png"), Description("Preferred format; omitted uses the available default.")] string? format = null,
        [FromQuery, Range(16, 256), Description("Raster width and height in pixels.")] int size = 96)
    {
        if (id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            Request.Query.ContainsKey("format") && string.IsNullOrEmpty(format)) return Respond(new(400, null));
        var icon = await ProviderArtworkResolver.ResolveIconAsync(HttpContext.RequestServices, id, Execution.CancellationToken, variant, format);
        if (icon == null) return Respond(new(404, null));
        var bytes = await HttpContext.RequestServices.GetRequiredService<ProviderIconRenderer>().ReadAsync(icon, size, variant == "glyph", Execution.CancellationToken);
        if (bytes == null) return Respond(new(404, null));
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(bytes);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=86400";
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag) ? StatusCode(304) :
            File(bytes, icon.ContentType == "image/svg+xml" ? icon.ContentType : "image/png");
    }
}
