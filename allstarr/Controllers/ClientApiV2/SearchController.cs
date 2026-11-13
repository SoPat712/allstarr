using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/search")]
public sealed class SearchController(V2SearchEndpoints search) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("search")]
    [EndpointDescription("Streams complete ranked snapshots as newline-delimited JSON. Each revision replaces the previous snapshot. " +
        "Songs use the native track shape; albums, artists and playlists are typed resources. Top also appears in its section. " +
        "The first snapshot waits for the preferred provider and another answer, or 400 ms; later snapshots are at least 250 ms apart. " +
        "The final snapshot has done=true and late provider ids. Provider work has a 2.75 second budget and final publishing a 3 second budget. " +
        "Streaming search is excluded from batch.")]
    [ProducesResponseType<V2SearchSnapshot>(200, "application/x-ndjson")]
    public Task<IActionResult> Get(
        [FromQuery, Description("Search text, from 1 to 200 characters after trimming.")] string? q,
        [FromQuery, Range(1, 200), Description("Maximum mixed results and results per section; defaults to 40.")] int limit = 40,
        [FromQuery, Description("Comma-separated songs, albums, artists and playlists; defaults to all four.")] string? types = null) =>
        search.StreamAsync(HttpContext, Execution, q, limit, types);

    [HttpGet("lyrics")]
    [EndpointName("searchLyricsV2")]
    [EndpointDescription("Searches stored lyrics visible to this listener, with matching lines and highlight offsets. Every result includes a native track. Supports conditional reads and batch.")]
    [ProducesResponseType<V2LyricsSearchResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Lyrics(
        [FromQuery, Description("Lyrics text, from 2 to 200 characters after trimming.")] string? q,
        [FromQuery, Range(1, 50), Description("Maximum results, from 1 to 50; defaults to 20.")] int limit = 20) =>
        Respond(await search.LyricsAsync(HttpContext, Execution, q, limit));
}
