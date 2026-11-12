using allstarr.Core.Artwork;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/providers/{providerId}/icon")]
public sealed class ProviderIconsController : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getProviderIcon")]
    [ProducesResponseType(typeof(byte[]), 200, "image/svg+xml", "image/png")]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string providerId, CancellationToken cancellationToken,
        [FromQuery, AllowedValues("color", "glyph")] string variant = "color",
        [FromQuery, AllowedValues("svg", "png")] string? format = null,
        [FromQuery, Range(16, 256)] int size = 96)
    {
        if (Request.Query.ContainsKey("format") && string.IsNullOrEmpty(format))
            return Problem(statusCode: 400, title: "Invalid icon format.");
        var icon = await ProviderArtworkResolver.ResolveIconAsync(HttpContext.RequestServices,
            providerId, cancellationToken, variant, format);
        if (icon == null) return NotFound();
        var bytes = await HttpContext.RequestServices.GetRequiredService<ProviderIconRenderer>().ReadAsync(icon, size,
            variant == "glyph", cancellationToken);
        if (bytes == null) return NotFound();
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(bytes);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, max-age=86400";
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)
            ? StatusCode(StatusCodes.Status304NotModified)
            : File(bytes, icon.ContentType == "image/svg+xml" ? icon.ContentType : "image/png");
    }
}
