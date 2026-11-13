using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/tracks/{id}")]
public sealed class TrackResourcesController(V2TrackResources tracks) : ClientApiV2ControllerBase
{
    [HttpGet("lyrics/sources")]
    [EndpointName("getTrackLyricsSources")]
    [EndpointDescription("Lists available lyrics versions, the effective choice and revision stamps. Can also be read inside a batch.")]
    [ProducesResponseType<ClientLyricsSources>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> LyricsSources(string id,
        [FromQuery, Description("Include each version's full rich lyrics.")] bool includeLyrics = false) =>
        Respond(await tracks.LyricsSourcesAsync(HttpContext, Execution, id, includeLyrics));

    [HttpPut("lyrics/source")]
    [EndpointName("chooseTrackLyricsSource")]
    [EndpointDescription("Chooses a lyrics version for this listener. Send the revision from the sources read; stale revisions return 409.")]
    [ProducesResponseType<ClientLyricsSelection>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> ChooseLyrics(string id, [FromBody] SetClientLyricsSourceRequest request) =>
        Respond(await tracks.ChooseLyricsAsync(HttpContext, Execution, id, request));

    [HttpDelete("lyrics/source")]
    [EndpointName("resetTrackLyricsSource")]
    [EndpointDescription("Clears this listener's lyrics choice, restoring the household choice or automatic selection. Send expectedRevision in the body; stale revisions return 409.")]
    [ProducesResponseType<ClientLyricsSelection>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> ResetLyrics(string id, [FromBody] ClearClientLyricsSourceRequest request) =>
        Respond(await tracks.ResetLyricsAsync(HttpContext, Execution, id, request));

    [HttpGet("match")]
    [EndpointName("getTrackMatch")]
    [EndpointDescription("Reads the effective match, available candidates and revision. Candidate ids are opaque choice handles. Can also be read inside a batch.")]
    [ProducesResponseType<ClientItemMatch>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Match(string id) => Respond(await tracks.MatchAsync(HttpContext, Execution, id));

    [HttpPut("match")]
    [EndpointName("chooseTrackMatch")]
    [EndpointDescription("Pins a match candidate for this listener. Send candidateId and expectedRevision from the match read; stale revisions return 409.")]
    [ProducesResponseType<ClientItemMatch>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> ChooseMatch(string id, [FromBody] SetClientMatchRequest request) =>
        Respond(await tracks.ChooseMatchAsync(HttpContext, Execution, id, request));

    [HttpDelete("match")]
    [EndpointName("resetTrackMatch")]
    [EndpointDescription("Clears this listener's match override, restoring the household or automatic match. Stale revisions return 409.")]
    [ProducesResponseType<ClientItemMatch>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> ResetMatch(string id,
        [FromQuery, Required, Description("Opaque revision from the match read.")] string expectedRevision) =>
        Respond(await tracks.ResetMatchAsync(HttpContext, Execution, id, expectedRevision));

    [HttpPost("match/reject")]
    [EndpointName("rejectTrackMatch")]
    [EndpointDescription("Rejects the effective match for this listener. Send expectedRevision from the match read; stale revisions return 409.")]
    [ProducesResponseType<ClientItemMatch>(200)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> RejectMatch(string id, [FromBody] RejectClientMatchRequest request) =>
        Respond(await tracks.RejectMatchAsync(HttpContext, Execution, id, request));

    [HttpGet("appearances")]
    [EndpointName("getTrackAppearances")]
    [EndpointDescription("Lists this listener's mixes and synced playlists containing the track, with v2 resource ids. Can also be read inside a batch.")]
    [ProducesResponseType<V2TrackAppearances>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Appearances(string id) => Respond(await tracks.AppearancesAsync(HttpContext, Execution, id));
}
