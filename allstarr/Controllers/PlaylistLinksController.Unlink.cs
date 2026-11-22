using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    // Ends the link and leaves the media-server playlist exactly as it is, for the listener to keep as their own.
    [HttpPost("{id:guid}/unlink")]
    public Task<IActionResult> Unlink(Guid id, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            if (link.OwnerUserId != session.AllstarrUserId) throw new UnauthorizedAccessException();
            try
            {
                var result = await orchestration.UnlinkAsync(await CreateExecutionAsync(session, cancellationToken), id, cancellationToken);
                return Ok(new { unlinked = true, playlistName = result.PlaylistName, keptSongs = result.KeptSongs, revision = result.Link.Revision });
            }
            catch (InvalidOperationException exception) { return Conflict(new { error = exception.Message }); }
        });

    [HttpGet("{id:guid}/library-additions")]
    public Task<IActionResult> PreviewLibraryAdditions(Guid id, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            var projection = await OwnLibraryAdditionsProjectionAsync(session, id, cancellationToken);
            if (projection == null) return NotFound();
            var plan = PlaylistLibraryAdditions.Plan(projection);
            return Ok(new
            {
                count = plan.Total,
                songs = plan.Songs.Select(song => new { title = song.Title, artist = song.Artist, album = song.Album, itemId = song.ItemId })
            });
        });

    [HttpPost("{id:guid}/library-additions")]
    public Task<IActionResult> AddToLibrary(Guid id, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            var projection = await OwnLibraryAdditionsProjectionAsync(session, id, cancellationToken);
            if (projection == null) return NotFound();
            var services = HttpContext.RequestServices;
            var execution = await CreateExecutionAsync(session, cancellationToken);
            // The same checks a single add makes up front, so a library that cannot take adds yet says so now.
            var capabilities = await services.GetRequiredService<ClientLibraryAdditions>().CapabilitiesAsync(execution, cancellationToken);
            if (capabilities.SetupNeeded != null) return Conflict(new { error = capabilities.SetupNeeded });
            try
            {
                var queued = await services.GetRequiredService<PlaylistLibraryAdditions>().QueueAsync(session.AllstarrUserId!.Value,
                    id, PlaylistLibraryAdditions.Plan(projection), HttpContext.TraceIdentifier, cancellationToken);
                return Ok(new { queued = queued.Queued, alreadyThere = queued.AlreadyThere, skipped = queued.Skipped, jobId = queued.JobId });
            }
            catch (PromotionException exception) { return Conflict(new { error = exception.Message }); }
        });

    private async Task<DurablePlaylistProjection?> OwnLibraryAdditionsProjectionAsync(allstarr.Services.Admin.AdminAuthSession session, Guid id,
        CancellationToken cancellationToken)
    {
        var link = await LoadScopedLink(session, id, cancellationToken);
        if (link.OwnerUserId != session.AllstarrUserId) throw new UnauthorizedAccessException();
        return await projections.ReadByLinkIdAsync(session.AllstarrUserId, id, cancellationToken, session.AllstarrUserId);
    }
}
