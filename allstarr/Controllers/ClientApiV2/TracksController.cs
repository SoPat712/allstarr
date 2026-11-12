using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/tracks")]
public sealed class TracksController(V2TrackEndpoints tracks) : ClientApiV2ControllerBase
{
    private const string IncludeDescription = "Comma-separated extras. `album` adds an `albums` map keyed by album id.";

    [HttpGet]
    [EndpointName("getTracks")]
    [EndpointDescription("Reads up to 100 tracks in the order asked. Any id spelling is accepted: a track id, a library item id or an " +
        "`ext-` service id. Two spellings of one song return it once, with both in `aliases`. Ids that are unknown or hidden for " +
        "this listener are listed in `missing`.")]
    [ProducesResponseType<V2TracksResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> List(
        [FromQuery, Required, Description("Comma-separated ids, at most 100.")] string ids,
        [FromQuery, Description(IncludeDescription)] string? include,
        [FromQuery, Description("`lyrics` adds each track's lyrics view and status, keyed by returned track id.")] string? views) =>
        Respond(await tracks.ListAsync(HttpContext, Execution, ids, include, views));

    [HttpGet("{id}")]
    [EndpointName("getTrack")]
    [EndpointDescription("Reads one track by any id spelling, with any requested views.")]
    [ProducesResponseType<V2TrackResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string id, [FromQuery, Description(IncludeDescription)] string? include,
        [FromQuery, Description("Comma-separated views: `lyrics`, `lyricsSources`, `match`, `autoplay`. `viewStatus` says how each one went; autoplay returns 25 tracks by default. Reading a track never opens its audio: get a stream link from `POST /allstarr/v2/playback/resolve`.")] string? views) =>
        Respond(await tracks.GetAsync(HttpContext, Execution, id, include, views));

    [HttpGet("{id}/lyrics/translation")]
    [EndpointName("getLyricsTranslation")]
    [EndpointDescription("Translates the same lines as `views=lyrics`, preserving their order for timing. Shared for 30 days per song, lyrics text and target language. Returns 404 when disabled or lyrics are absent.")]
    [ProducesResponseType<V2LyricsTranslationResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Translation(string id,
        [FromQuery, Description("Target BCP-47 language, such as `en`, `es` or `pt-BR`. Defaults to `en`.")] string? to) =>
        Respond(await tracks.TranslationAsync(HttpContext, Execution, id, to));

    [HttpGet("{id}/autoplay")]
    [EndpointName("getTrackAutoplay")]
    [EndpointDescription("Continues after a track: InstantMix or service radio, similar songs, the same artist, then the album. Follow `next` to extend the queue without repeats; a partial page still has a cursor. Continuations retain at most a few hundred track identifiers.")]
    [ProducesResponseType<V2AutoplayResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Autoplay(string id,
        [FromQuery, Description("Opaque cursor from the previous page.")] string? cursor,
        [FromQuery, Range(1, 50), Description("Tracks per page, from 1 to 50; defaults to 25.")] int limit = 25) =>
        Respond(await tracks.AutoplayAsync(HttpContext, Execution, id, cursor ?? (Request.Query.ContainsKey("cursor") ? "" : null), limit));
}
