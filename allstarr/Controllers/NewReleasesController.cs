using allstarr.Core.Releases;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/releases")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class NewReleasesController(NewReleasesService releases) : ControllerBase
{
    [HttpGet("artists")]
    public Task<IActionResult> List(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.ListAsync(owner, cancellationToken)));

    [HttpPut("artists")]
    public Task<IActionResult> Follow([FromBody] FollowReleaseArtistRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async owner =>
        {
            await releases.FollowAsync(owner, new(request.ProviderId, request.ArtistId, request.DisplayName), cancellationToken);
            return NoContent();
        });

    [HttpDelete("artists/{providerId}/{artistId}")]
    public Task<IActionResult> Unfollow(string providerId, string artistId, CancellationToken cancellationToken) =>
        ExecuteAsync(async owner =>
        {
            await releases.UnfollowAsync(owner, providerId, artistId, cancellationToken);
            return NoContent();
        });

    [HttpPost("artists/from-hearts")]
    public Task<IActionResult> FollowHearted(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(new { followed = await releases.FollowHeartedArtistsAsync(owner, cancellationToken) }));

    [HttpGet]
    public Task<IActionResult> Inbox(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.ReadAsync(owner, cancellationToken)));

    [HttpDelete("{entryId}")]
    public Task<IActionResult> Dismiss(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        await releases.DismissAsync(owner, entryId, cancellationToken) ? NoContent() : NotFound());

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<IActionResult>> action)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (session.AllstarrUserId is not { } owner)
            return StatusCode(403, new { error = "A linked backend user is required." });
        try { return await action(owner); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked listener is required." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The followed artist is invalid." }); }
    }
}

public sealed record FollowReleaseArtistRequest(string ProviderId, string ArtistId, string DisplayName);
