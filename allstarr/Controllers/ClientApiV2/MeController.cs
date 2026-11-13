using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/me")]
public sealed class MeController(V2MeEndpoints me, RecapArtworkReader artwork, JellyfinProxyService jellyfin) : ClientApiV2ControllerBase
{
    [HttpGet("history")]
    [EndpointName("getMyHistoryV2")]
    [EndpointDescription("Reads this listener's qualified plays, newest first. Current songs use V2Track; unavailable songs retain their recorded facts with a null track. Can be batched.")]
    [ProducesResponseType<V2HistoryPage>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> History(
        [FromQuery, Range(1, 200), Description("Plays per page, from 1 to 200; defaults to 50.")] int limit = 50,
        [FromQuery, Description("Opaque next cursor from the previous page.")] string? before = null) =>
        Respond(await me.ReadAsync(HttpContext, Execution, "history"));

    [HttpGet("history/imports")]
    [EndpointName("getMyHistoryImportsV2")]
    [EndpointDescription("Reads this listener's latest 50 import previews, counts and job status without changing them. Can be batched.")]
    [ProducesResponseType<IReadOnlyList<ListeningHistoryImportPreviewResult>>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Imports() => Respond(await me.ReadAsync(HttpContext, Execution, "history/imports"));

    [HttpGet("history/imports/recheck")]
    [EndpointName("getMyHistoryRecheckV2")]
    [EndpointDescription("Checks this listener's imported matches with the current matcher, without changing them. Examples include matched V2Track values when available. Can be batched.")]
    [ProducesResponseType<V2HistoryRecheck>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Recheck() => Respond(await me.ReadAsync(HttpContext, Execution, "history/imports/recheck"));

    [HttpGet("stats")]
    [EndpointName("getMyStatsV2")]
    [EndpointDescription("Reads this listener's listening counts, timeline and ranked songs, artists and albums with artwork URLs. Can be batched.")]
    [ProducesResponseType<V2ListeningStats>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Stats(
        [FromQuery, Description("7d, 30d (default), year or all. Cannot be combined with year.")] string? period = null,
        [FromQuery, Range(1, 9998), Description("A calendar year instead of period.")] int? year = null,
        [FromQuery, Description("Time zone for calendar boundaries, such as America/New_York.")] string? timeZoneId = null) =>
        Respond(await me.ReadAsync(HttpContext, Execution, "stats"));

    [HttpGet("sound")]
    [EndpointName("getMySoundV2")]
    [EndpointDescription("Reads this listener's sound traits from stored analysis. Returns 404 until enough songs have been analyzed. Can be batched.")]
    [ProducesResponseType<SoundProfile>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Sound() => Respond(await me.ReadAsync(HttpContext, Execution, "sound"));

    [HttpGet("taste")]
    [EndpointName("getMyTasteV2")]
    [EndpointDescription("Reads this listener's recent and usual tastes, nudges, paused state and imported-history preference. Can be batched.")]
    [ProducesResponseType<V2TasteProfile>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Taste() => Respond(await me.ReadAsync(HttpContext, Execution, "taste"));

    [HttpGet("preferences")]
    [EndpointName("getPreferencesV2")]
    [EndpointDescription("Reads this listener's heart button action; defaults to keep. Can be batched.")]
    [ProducesResponseType<V2Preferences>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Preferences() => Respond(await me.ReadAsync(HttpContext, Execution, "preferences"));

    [HttpPatch("preferences")]
    [EndpointName("updatePreferencesV2")]
    [EndpointDescription("Changes only this listener's heart button action: heart, keep or addToLibrary.")]
    [ProducesResponseType<V2Preferences>(200)]
    [ProducesResponseType(409)]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> UpdatePreferences([FromBody] V2Preferences request) =>
        Respond(await me.UpdatePreferencesAsync(Execution, request));

    [HttpGet("stats/art")]
    [EndpointName("getMyStatsArtworkV2")]
    [EndpointDescription("Serves the artwork URL embedded in listening stats and history using the listener's sources. Cannot be batched.")]
    [ProducesResponseType(typeof(byte[]), 200, "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Art(
        [FromQuery, Required, StringLength(500), Description("Artist name from the listening entry.")] string artist,
        [FromQuery, StringLength(500), Description("Song title for song artwork.")] string? title = null,
        [FromQuery, StringLength(500), Description("Album title for album artwork.")] string? album = null,
        [FromQuery, Description("Library artist ID for artist artwork, copied from the response URL.")] string? libraryArtistId = null)
    {
        string? source = null;
        if (libraryArtistId != null)
        {
            if (!V2Ids.TryDecode(libraryArtistId, V2IdKind.Artist, out var decoded) || decoded.Source!.StartsWith("ext-", StringComparison.Ordinal))
                return Respond(new(400, null));
            source = decoded.Source;
            var (document, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(Execution.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(source!)}",
                new(), Request.Headers, Execution.CancellationToken);
            using var visible = document;
            if (status is 400 or 401 or 403 or 404) return Respond(new(404, null));
            if (status != 200) return Respond(new(503, null));
            if (document == null || !document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MusicArtist") return Respond(new(404, null));
        }
        try
        {
            var asset = await artwork.ReadAsync(Execution, artist, title, album, Execution.CancellationToken, source);
            Response.Headers.CacheControl = asset == null ? "private, max-age=3600" : "private, max-age=604800";
            return asset == null ? NoContent() : File(asset.Bytes, asset.ContentType);
        }
        catch (ArgumentException) { return Respond(new(400, null)); }
        catch (UnauthorizedAccessException) { return Respond(new(404, null)); }
    }
}
