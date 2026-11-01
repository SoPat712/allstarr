using System.Text.Json;
using allstarr.Core.Playlists;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed partial class PersonalMixService
{
    // Image loaders may omit their player token. Resolve only already published artwork
    // from its stored owner; never load/migrate a listener document on this public path.
    public async Task<MixCoverInput?> PublishedCoverAsync(Guid id, CancellationToken token = default)
    {
        if (!(bool)(await settings.GetAsync(PublishKey, token)).Value) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (link != null)
        {
            if (!link.Enabled || link.TargetProtocol != "jellyfin" || !IsExternalLink(link) ||
                !await db.Users.AsNoTracking().AnyAsync(user => user.Id == link.OwnerUserId && user.Enabled && !user.IsSynthetic &&
                    user.BackendType == link.TargetProtocol && user.BackendInstanceId == link.TargetBackendInstanceId, token) ||
                !await db.ProviderAccounts.AsNoTracking().AnyAsync(account =>
                    account.Id == link.ProviderAccountId && account.OwnerUserId == link.OwnerUserId &&
                    account.ProviderId == link.SourceProviderId && account.Enabled, token)) return null;
            var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().Where(item =>
                    item.PlaylistLinkId == id && item.OwnerUserId == link.OwnerUserId && item.PublishedAt.HasValue)
                .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
            if (snapshot == null) return null;
            var count = await db.PlaylistSourceEntries.CountAsync(item => item.PlaylistSourceSnapshotId == snapshot.Id, token);
            return new(link.Id.ToString(), snapshot.SnapshotVersion, "external", Source: link.SourceProviderId,
                SourcePlaylistId: link.SourcePlaylistId, Name: PlaylistVirtualizationService.DisplayName(link.SourceProviderId, snapshot.Name),
                Count: count);
        }

        // Personal mixes use IDs inside each owner's versioned document, rather than
        // playlist_links. Select the latest document before checking Added or the ID.
        var rows = await db.ExternalMetadataSnapshots.AsNoTracking().Where(row =>
                row.ProviderId == "allstarr" && row.ResourceKind == ResourceKind && row.Protocol == "jellyfin" &&
                db.Users.Any(user => user.Id == row.OwnerUserId && user.Enabled && !user.IsSynthetic &&
                    user.BackendType == row.Protocol && user.BackendInstanceId == row.BackendInstanceId &&
                    user.BackendPrincipalId == row.BackendPrincipalId))
            .GroupBy(row => row.OwnerUserId).Select(group => group.OrderByDescending(row => row.SnapshotVersion).First())
            .ToArrayAsync(token);
        foreach (var row in rows)
        {
            var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson);
            var mix = document?.Mixes.Values.SingleOrDefault(item => item.Id == id);
            if (mix is { Added: true, Generation: > 0 })
                return MixCoverInput.From(mix);
        }
        return null;
    }
}
