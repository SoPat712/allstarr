using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/artists/{libraryArtistId}/links")]
public sealed class ArtistLinksController(V2ArtistLinkEndpoints links) : ClientApiV2ControllerBase
{
    private const string Ids = " Ids may be v2 artist ids or the older media server and `ext-` artist ids.";

    [HttpGet]
    [EndpointName("getArtistLinks")]
    [EndpointDescription("The service artists shown as this library artist, tied by matched songs or by a person's answer, and the ones someone said are a different act." + Ids)]
    [ProducesResponseType<V2ArtistLinksResponse>(200)]
    public async Task<IActionResult> Get(string libraryArtistId) =>
        Respond(await links.ReadAsync(HttpContext, Execution, libraryArtistId));

    [HttpPut("{serviceArtistId}")]
    [EndpointName("setArtistLink")]
    [EndpointDescription("Says whether a service artist is the same artist as this library artist. An administrator's answer covers the household; " +
        "a listener's covers only them and wins over the household's for them. Search and artist pages follow at once." + Ids)]
    [ProducesResponseType<V2ArtistLinksResponse>(200)]
    public async Task<IActionResult> Put(string libraryArtistId, string serviceArtistId, [FromBody] V2ArtistLinkRequest request) =>
        Respond(await links.SetAsync(HttpContext, Execution, libraryArtistId, serviceArtistId, request.Same));

    [HttpDelete("{serviceArtistId}")]
    [EndpointName("clearArtistLink")]
    [EndpointDescription("Takes back this person's answer, so matched songs decide again. A listener cannot take back the household's answer (403); " +
        "they can mark it as not the same for themselves instead." + Ids)]
    [ProducesResponseType<V2ArtistLinksResponse>(200)]
    public async Task<IActionResult> Delete(string libraryArtistId, string serviceArtistId) =>
        Respond(await links.SetAsync(HttpContext, Execution, libraryArtistId, serviceArtistId, null));
}
