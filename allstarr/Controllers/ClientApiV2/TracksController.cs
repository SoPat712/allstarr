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
    public async Task<IActionResult> List(
        [FromQuery, Required, Description("Comma-separated ids, at most 100.")] string ids,
        [FromQuery, Description(IncludeDescription)] string? include) =>
        Respond(await tracks.ListAsync(HttpContext, Execution, ids, include));

    [HttpGet("{id}")]
    [EndpointName("getTrack")]
    [EndpointDescription("Reads one track by any id spelling, with any requested views.")]
    [ProducesResponseType<V2TrackResponse>(200)]
    public async Task<IActionResult> Get(string id, [FromQuery, Description(IncludeDescription)] string? include,
        [FromQuery, Description("Comma-separated views: `lyrics`, `lyricsSources`, `match`. `viewStatus` says how each one went.")] string? views) =>
        Respond(await tracks.GetAsync(HttpContext, Execution, id, include, views));
}
