using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Models.Domain;
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
    IMediaAssetResolver media, IHttpClientFactory clients, ILogger<ReleaseArtworkController> logger) : ControllerBase
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    [HttpGet("{providerId}/{albumId}")]
    public async Task<IActionResult> Get(string providerId, string albumId, CancellationToken token)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (session.AllstarrUserId is not { } owner) return StatusCode(403, new { error = "A linked backend user is required." });
        if (!Valid(providerId, 100) || !Valid(albumId, 300)) return BadRequest();
        ProtocolExecutionContext protocol;
        try { protocol = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked listener is required." }); }
        var asset = await media.ResolveAsync(new(owner, null, providerId, "album", albumId, Width: 384),
            cancel => FetchAsync(protocol, providerId, albumId, cancel), MaximumBytes, token);
        // An empty answer shows the placeholder without a broken image, and a missing cover is asked for again later.
        Response.Headers.CacheControl = asset == null ? "private, max-age=3600" : "private, max-age=604800";
        return asset == null ? NoContent() : File(asset.Bytes, asset.ContentType);
    }

    private async Task<MediaAssetSource?> FetchAsync(ProtocolExecutionContext protocol, string providerId, string albumId, CancellationToken token)
    {
        try
        {
            // Album details already kept for this listener answer without asking the source again.
            var url = Cover(await gateway.GetStoredAlbumAsync(protocol, providerId, albumId), providerId) ??
                Cover(await gateway.GetAlbumAsync(protocol, providerId, albumId), providerId);
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
            using var response = await clients.CreateClient().GetAsync(uri, token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
            return new(await response.Content.ReadAsByteArrayAsync(token), contentType);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            // A source that is turned off or failing for this listener leaves the placeholder.
            logger.LogDebug(exception, "Release artwork lookup failed for provider {ProviderId}", providerId);
            return null;
        }
    }

    // Albums without art carry the source's logo instead; a release keeps its placeholder rather than show that.
    private static string? Cover(Album? album, string providerId) =>
        album == null || ProviderArtworkFallback.IsFallback(providerId, album.CoverArtUrl, "album") ? null : album.CoverArtUrl;

    private static bool Valid(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}
