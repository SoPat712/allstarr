using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/albums")]
public sealed class AlbumsController(V2AlbumEndpoints albums) : ClientApiV2ControllerBase
{
    [HttpGet("{id}/similar")]
    [EndpointName("getAlbumSimilar")]
    [EndpointDescription("Reads related artists' albums using the library copy when available, with v2 album shapes. Can also be read inside a batch.")]
    [ProducesResponseType<V2SimilarAlbumsResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Similar(string id,
        [FromQuery, Range(1, 24), Description("Albums to return, from 1 to 24; defaults to 12.")] int limit = 12) =>
        Respond(await albums.SimilarAsync(HttpContext, Execution, id, limit));

    [HttpGet("{id}")]
    [EndpointName("getAlbum")]
    [EndpointDescription("Reads a library or service album by its native or older id. Can also be read inside a batch.")]
    [ProducesResponseType<V2AlbumResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string id,
        [FromQuery, Description("`tracks` includes the album's native track shapes in disc and track order.")] string? include,
        [FromQuery, Description("`more-by-artist` includes up to 20 other album summaries by the first identified album artist.")] string? views) =>
        Respond(await albums.GetAsync(HttpContext, Execution, id, include, views));
}
