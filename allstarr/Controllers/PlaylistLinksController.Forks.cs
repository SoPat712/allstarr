using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    [HttpGet("/api/admin/listeners/{listenerId:guid}/playlist-forks")]
    public async Task<IActionResult> ListPlaylistForks(Guid listenerId, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            EnsureSessionScope(session, listenerId);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Users.AnyAsync(item => item.Id == listenerId, cancellationToken)) return NotFound();
            var forks = await HttpContext.RequestServices.GetRequiredService<WritablePlaylistMembership>()
                .ListForksAsync(listenerId, cancellationToken);
            return Ok(forks.Select(item => new
            {
                id = item.Playlist.ProtocolId,
                name = item.Playlist.Name,
                originalPlaylistId = item.OriginalPlaylistId,
                originalTargetPlaylistId = item.OriginalTargetPlaylistId,
                backendInstanceId = item.BackendInstanceId,
                createdAt = item.CreatedAt
            }));
        });
    }
}
