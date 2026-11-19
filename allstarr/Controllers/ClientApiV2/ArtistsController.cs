using System.ComponentModel;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/artists")]
public sealed class ArtistsController(V2ArtistEndpoints artists) : ClientApiV2ControllerBase
{
    [HttpGet("{id}")]
    [EndpointName("getArtist")]
    [EndpointDescription("Reads a library or service artist by its native or older id. Can also be read inside a batch.")]
    [ProducesResponseType<V2ArtistResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get(string id,
        [FromQuery, Description("`albums` adds up to 50 of the artist's albums, for a service artist also its albums from the listener's other services; `tracks` adds up to 20 top songs as native track shapes.")] string? include) =>
        Respond(await artists.GetAsync(HttpContext, Execution, id, include));
}
