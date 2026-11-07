using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed partial class PlaylistLinksController
{
    [HttpGet("membership")]
    public Task<IActionResult> Membership(
        [FromQuery] Guid? canonicalRecordingId,
        [FromQuery] string? providerId,
        [FromQuery] string? externalId,
        [FromQuery] string? protocol,
        [FromQuery] string? backendInstanceId,
        [FromQuery] string? backendItemId,
        CancellationToken cancellationToken) => Execute(async session =>
    {
        var memberships = await new SongPlaylistMembershipReader(contextFactory).ReadAsync(
            session.AllstarrUserId!.Value,
            new(canonicalRecordingId, providerId, externalId, protocol, backendInstanceId, backendItemId),
            cancellationToken);
        return Ok(new { playlists = memberships });
    });
}
