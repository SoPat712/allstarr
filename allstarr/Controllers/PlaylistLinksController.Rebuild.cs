using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    [HttpPost("{id:guid}/rebuild")]
    public async Task<IActionResult> Rebuild(Guid id, CancellationToken cancellationToken)
    {
        return await Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            if (link.ImportMode == PlaylistImportMode.OneTime)
                return Conflict(new { error = "Imports keep your own order." });
            if (link.TargetProtocol != "jellyfin" || link.Mode == PlaylistLinkMode.Virtual || link.TargetPlaylistId == null)
                return Conflict(new { error = "Rebuild requires a mapped Jellyfin playlist." });
            try
            {
                var updated = await orchestration.RebuildAsync(await CreateExecutionAsync(session, cancellationToken), id, cancellationToken);
                return Ok(ToDto(updated));
            }
            catch (PlaylistRebuildException exception) { return StatusCode(502, new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Conflict(new { error = exception.Message }); }
        });
    }
}
