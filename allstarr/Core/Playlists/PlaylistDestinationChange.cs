using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

internal static class PlaylistDestinationChange
{
    public static async Task ApplyAsync(AllstarrDbContext db, PlaylistLinkRecord link, string? targetPlaylistId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.Equals(link.TargetPlaylistId, targetPlaylistId, StringComparison.Ordinal)) return;
        link.DestinationMode = targetPlaylistId == null ? PlaylistDestinationMode.New : PlaylistDestinationMode.Existing;
        var memberships = await db.PlaylistTargetMemberships
            .Where(item => item.PlaylistLinkId == link.Id && (item.Active || item.RemovalConfirmedAt == null))
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            membership.Active = false;
            membership.RemovalConfirmedAt = now;
            membership.UpdatedAt = now;
            membership.Revision++;
        }
    }
}
