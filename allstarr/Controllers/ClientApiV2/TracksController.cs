using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/tracks")]
public sealed class TracksController(V2TrackReader reader) : ClientApiV2ControllerBase
{
    private const string IncludeDescription = "Comma-separated extras. `album` adds an `albums` map keyed by album id.";
    private static readonly IReadOnlySet<string> Includes = new HashSet<string>(StringComparer.Ordinal) { "album" };

    [HttpGet]
    [EndpointName("getTracks")]
    [EndpointDescription("Reads up to 100 tracks in the order asked. Any id spelling is accepted: a track id, a library item id or an " +
        "`ext-` service id. Two spellings of one song return it once, with both in `aliases`. Ids that are unknown or hidden for " +
        "this listener are listed in `missing`.")]
    [ProducesResponseType<V2TracksResponse>(200)]
    public async Task<ActionResult<V2TracksResponse>> List(
        [FromQuery, Required, Description("Comma-separated ids, at most 100.")] string ids,
        [FromQuery, Description(IncludeDescription)] string? include)
    {
        if (!TryIds(ids, out var requested) || !TryInclude(include, Includes, out var included)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await reader.ReadAsync(HttpContext, Execution, requested, included.Contains("album")));
    }

    [HttpGet("{id}")]
    [EndpointName("getTrack")]
    [EndpointDescription("Reads one track by any id spelling.")]
    [ProducesResponseType<V2TrackResponse>(200)]
    public async Task<ActionResult<V2TrackResponse>> Get(string id, [FromQuery, Description(IncludeDescription)] string? include)
    {
        if (!TryInclude(include, Includes, out var included)) return BadRequest();
        var result = await reader.ReadAsync(HttpContext, Execution, [id], included.Contains("album"));
        if (result.Tracks.Count == 0) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(new V2TrackResponse(result.Tracks[0]) { Albums = result.Albums });
    }
}
