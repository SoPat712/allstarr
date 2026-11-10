using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Core.Releases;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

// A release cover is public catalog art, so it is looked up by the release's source album with the listener's own
// provider access, the same way their apps open that album, and kept once found.
[ApiController]
[Route("api/admin/releases/artwork")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ReleaseArtworkController(AdminProtocolExecutionContextFactory contexts, IProtocolProviderGateway gateway,
    IMediaAssetResolver media, IHttpClientFactory clients, ILogger<ReleaseArtworkController> logger, IApplicationCache? cache = null) : ControllerBase
{
    [HttpGet("{providerId}/{albumId}")]
    public async Task<IActionResult> Get(string providerId, string albumId, CancellationToken token)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (session.AllstarrUserId is not { } owner) return StatusCode(403, new { error = "A linked backend user is required." });
        if (!Valid(providerId, 100) || !Valid(albumId, 300)) return BadRequest();
        ProtocolExecutionContext protocol;
        try { protocol = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked listener is required." }); }
        ResolvedMediaAsset? asset;
        try
        {
            asset = await new ReleaseArtworkReader(gateway, media, clients, logger, cache)
                .ReadAsync(owner, protocol, providerId, albumId, token, $"{providerId}:{albumId}");
        }
        catch (TimeoutException)
        {
            Response.Headers.CacheControl = "private, max-age=60";
            return NoContent();
        }
        // An empty answer shows the placeholder without a broken image, and a missing cover is asked for again later.
        Response.Headers.CacheControl = asset == null ? "private, max-age=3600" : "private, max-age=604800";
        if (asset == null) return NoContent();
        var etag = ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes);
        Response.Headers.ETag = etag;
        return ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)
            ? StatusCode(StatusCodes.Status304NotModified) : File(asset.Bytes, asset.ContentType);
    }

    private static bool Valid(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}
