using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    [HttpDelete("{id:guid}/in-apps")]
    public Task<IActionResult> RemoveFromApps(Guid id, [FromBody] PlaylistAppRequest request, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            if (link.OwnerUserId != session.AllstarrUserId) throw new UnauthorizedAccessException();
            try
            {
                var removed = await orchestration.RemoveFromAppsAsync(await CreateExecutionAsync(session, cancellationToken),
                    id, request.ExpectedRevision, cancellationToken);
                return Ok(new { inApps = false, revision = removed.Revision });
            }
            catch (PlaylistAppRemovalException exception) { return StatusCode(502, new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Conflict(new { error = exception.Message }); }
        });

    [HttpPost("{id:guid}/in-apps")]
    public Task<IActionResult> AddToApps(Guid id, [FromBody] PlaylistAppRequest request, CancellationToken cancellationToken) =>
        Execute(async session =>
        {
            var link = await LoadScopedLink(session, id, cancellationToken);
            if (link.OwnerUserId != session.AllstarrUserId) throw new UnauthorizedAccessException();
            var restored = await orchestration.RestoreToAppsAsync(await CreateExecutionAsync(session, cancellationToken),
                id, request.ExpectedRevision, cancellationToken);
            return Ok(ToDto(restored.Link, restored.JobId));
        });
}

public sealed record PlaylistAppRequest(long ExpectedRevision);
