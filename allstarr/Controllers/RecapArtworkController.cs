using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/stats/art")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class RecapArtworkController(AdminProtocolExecutionContextFactory contexts, RecapArtworkReader artwork, JellyfinProxyService jellyfin) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? artist, [FromQuery] string? title = null, [FromQuery] string? album = null, CancellationToken token = default, [FromQuery] string? libraryArtistId = null)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 500 || title?.Length > 500 || album?.Length > 500) return BadRequest();
        var protocol = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
        if (libraryArtistId != null)
        {
            if (protocol.Protocol != ProtocolKind.Jellyfin || string.IsNullOrWhiteSpace(libraryArtistId) ||
                libraryArtistId.Length > 500 || libraryArtistId.Any(char.IsControl)) return BadRequest();
            var headers = new HeaderDictionary { ["X-Emby-Token"] = session.JellyfinAccessToken };
            var (body, status) = await jellyfin.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(protocol.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(libraryArtistId)}",
                clientHeaders: headers, cancellationToken: token);
            using (body)
            {
                if (status == 200 && body != null && body.RootElement.TryGetProperty("Id", out var id) &&
                    id.GetString() == libraryArtistId && body.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "MusicArtist")
                {
                    var tag = body.RootElement.TryGetProperty("ImageTags", out var tags) && tags.TryGetProperty("Primary", out var primary)
                        ? primary.GetString() : null;
                    var image = await jellyfin.GetImageAsync(libraryArtistId, imageTag: tag, clientHeaders: headers);
                    if (image.Body != null && image.ContentType != null)
                    {
                        Response.Headers.CacheControl = "private, max-age=3600";
                        return File(image.Body, image.ContentType);
                    }
                }
            }
        }
        var asset = await artwork.ReadAsync(protocol, artist, title, album, token, libraryArtistId);
        Response.Headers.CacheControl = asset != null ? "private, max-age=604800" : "private, max-age=3600";
        return asset != null ? File(asset.Bytes, asset.ContentType) : NoContent();
    }
}
