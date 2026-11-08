using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/releases")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class NewReleasesController(NewReleasesService releases, AdminProtocolExecutionContextFactory? protocolContexts = null,
    IProtocolProviderGateway? providers = null) : ControllerBase
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

    [HttpGet("artists/search")]
    public Task<IActionResult> Search([FromQuery] string? query, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.SearchArtistsAsync(owner, query ?? "", cancellationToken)));

    [HttpPost("artists/from-history")]
    public Task<IActionResult> FollowFromHistory(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(new { followed = await releases.FollowTopArtistsAsync(owner, cancellationToken) }));

    [HttpGet]
    public Task<IActionResult> Inbox(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.ReadAsync(owner, cancellationToken)));

    // The songs on a release, from the first source that can list them.
    [HttpGet("{entryId}/tracks")]
    public Task<IActionResult> Tracks(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
    {
        var inbox = await releases.ReadAsync(owner, cancellationToken);
        var entry = inbox.Entries.Concat(inbox.Excluded).FirstOrDefault(item => item.Id == entryId);
        if (entry == null) return NotFound();
        if (protocolContexts == null || providers == null) return StatusCode(503, new { error = "Track lists are unavailable." });
        var session = (AdminAuthSession)HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey]!;
        var execution = (await protocolContexts.CreateAsync(session, HttpContext.TraceIdentifier, cancellationToken)) with { IsReadOnly = false };
        var primary = ReleaseInboxProjection.PrimarySource(entry.Sources);
        foreach (var source in entry.Sources.OrderBy(item => item == primary ? 0 : 1))
        {
            var album = await providers.GetAlbumAsync(execution, source.ProviderId, source.AlbumId);
            if (album is not { Songs.Count: > 0 }) continue;
            return Ok(new
            {
                providerId = source.ProviderId,
                tracks = album.Songs.Select((song, index) => new
                {
                    number = song.Track ?? index + 1,
                    title = song.Title,
                    artists = song.Artists.Count > 0 ? string.Join(", ", song.Artists) : song.Artist,
                    durationMs = song.Duration * 1000L,
                    isExplicit = song.ExplicitContentLyrics == 1
                }).ToArray()
            });
        }
        return Ok(new { providerId = (string?)null, tracks = Array.Empty<object>() });
    });

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
