using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/artists/{id}/follow")]
public sealed class ArtistFollowController(V2LibraryEndpoints library) : ClientApiV2ControllerBase
{
    [HttpPut]
    [EndpointName("followV2Artist")]
    [EndpointDescription("Follows an accessible library or service artist for this listener. Accepts native or older artist ids. Repeating the request has the same result.")]
    [ProducesResponseType<V2ArtistFollow>(200)]
    public async Task<IActionResult> Follow(string id) => Respond(await library.FollowAsync(HttpContext, Execution, id, true));

    [HttpDelete]
    [EndpointName("unfollowV2Artist")]
    [EndpointDescription("Unfollows an accessible artist for this listener. Other listeners' follows are unchanged. Repeating the request has the same result.")]
    [ProducesResponseType<V2ArtistFollow>(200)]
    public async Task<IActionResult> Unfollow(string id) => Respond(await library.FollowAsync(HttpContext, Execution, id, false));
}
