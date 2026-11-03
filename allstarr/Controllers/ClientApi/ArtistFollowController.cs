using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/artists/{artistId}/follow")]
public sealed class ArtistFollowController(ReleaseReader reader) : ClientApiControllerBase
{
    private static readonly ClientApiError MissingArtist = new("artist_not_found", "The artist could not be resolved for this listener.");

    [HttpPut]
    [EndpointName("followArtist")]
    [ProducesResponseType<ClientArtistFollow>(200)]
    public Task<ActionResult<ClientArtistFollow>> Follow(string artistId) =>
        ExecuteAsync(() => reader.FollowAsync(HttpContext, Execution, artistId, true));

    [HttpDelete]
    [EndpointName("unfollowArtist")]
    [ProducesResponseType<ClientArtistFollow>(200)]
    public Task<ActionResult<ClientArtistFollow>> Unfollow(string artistId) =>
        ExecuteAsync(() => reader.FollowAsync(HttpContext, Execution, artistId, false));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T?>> action) where T : class
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await action();
            return result == null ? NotFound(MissingArtist) : Ok(result);
        }
        catch (UnauthorizedAccessException) { return NotFound(MissingArtist); }
        catch (KeyNotFoundException) { return NotFound(MissingArtist); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
