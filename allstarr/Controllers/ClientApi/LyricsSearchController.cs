using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/search/lyrics")]
public sealed class LyricsSearchController(LyricsSearchReader reader) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("searchLyrics")]
    [ProducesResponseType<LyricsSearchResponse>(200)]
    public async Task<ActionResult<LyricsSearchResponse>> Get([FromQuery, Required] string q, [FromQuery, Range(1, 50)] int limit = 20)
    {
        Response.Headers.CacheControl = "no-store";
        var query = q.Trim();
        if (query.Length is < 2 or > 200) return BadRequest();
        return Ok(await reader.ReadAsync(HttpContext, Execution, query, limit));
    }
}
