using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record PlaylistForkState(string OriginalPlaylistId, string? OriginalTargetPlaylistId,
    string BackendInstanceId, DateTimeOffset CreatedAt, VirtualPlaylistReadModel Playlist);

public sealed partial class WritablePlaylistMembership
{
    private const string ForkResourceKind = "playlist-fork";

    public async Task<PlaylistLinkRecord?> FindImportedPlaylistAsync(ProtocolExecutionContext context, string id,
        CancellationToken cancellationToken)
    {
        if (context.Protocol != ProtocolKind.Jellyfin) return null;
        var owner = context.Actor?.EffectiveUserId;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var links = await db.PlaylistLinks.AsNoTracking().Where(item => item.TargetProtocol == "jellyfin" &&
                item.TargetBackendInstanceId == context.BackendInstanceId && item.ImportMode == PlaylistImportMode.Linked &&
                (owner == null || item.OwnerUserId == owner || item.TargetPlaylistId != null))
            .ToArrayAsync(cancellationToken);
        return links.FirstOrDefault(item => SamePlaylistId(id, PlaylistVirtualizationService.CreateProtocolId(item.Id)) ||
            item.TargetPlaylistId != null && SamePlaylistId(id, item.TargetPlaylistId) ||
            SamePlaylistId(id, PlaylistIdHelper.CreatePlaylistId(item.SourceProviderId, item.SourcePlaylistId)));
    }

    public async Task<IReadOnlyList<PlaylistForkState>> ListForksAsync(Guid listenerId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == listenerId &&
            item.ProviderId == "allstarr" && item.Protocol == "jellyfin" && item.ResourceKind == ForkResourceKind);
        var rows = await query.Where(item => !query.Any(newer => newer.ExternalIdHash == item.ExternalIdHash &&
            newer.SnapshotVersion > item.SnapshotVersion)).ToArrayAsync(cancellationToken);
        return rows.Select(item => JsonSerializer.Deserialize<PlaylistForkState>(item.PayloadJson)!).ToArray();
    }

    public async Task<PlaylistForkState?> ReadForkAsync(ProtocolExecutionContext context, string id,
        CancellationToken cancellationToken)
    {
        if (context.Protocol != ProtocolKind.Jellyfin || context.Actor?.EffectiveUserId is not { } owner) return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(user => user.Id == owner && user.Enabled && user.BackendType == "jellyfin" &&
                user.BackendInstanceId == context.BackendInstanceId && user.BackendPrincipalId == context.VerifiedBackendPrincipalId,
                cancellationToken)) return null;
        var query = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.ProviderId == "allstarr" && item.Protocol == "jellyfin" && item.ResourceKind == ForkResourceKind &&
            item.BackendInstanceId == context.BackendInstanceId && item.BackendPrincipalId == context.VerifiedBackendPrincipalId);
        var rows = await query.Where(item => !query.Any(newer => newer.ExternalIdHash == item.ExternalIdHash &&
            newer.SnapshotVersion > item.SnapshotVersion)).ToArrayAsync(cancellationToken);
        var forks = rows.Select(item => JsonSerializer.Deserialize<PlaylistForkState>(item.PayloadJson)!).ToArray();
        var direct = forks.FirstOrDefault(item =>
            SamePlaylistId(id, item.Playlist.ProtocolId) || SamePlaylistId(id, item.OriginalPlaylistId) ||
            item.OriginalTargetPlaylistId != null && SamePlaylistId(id, item.OriginalTargetPlaylistId) ||
            SamePlaylistId(id, PlaylistIdHelper.CreatePlaylistId(item.Playlist.SourceProviderId, item.Playlist.SourcePlaylistId)));
        if (direct != null || forks.Length == 0) return direct;
        var link = await FindImportedPlaylistAsync(context, id, cancellationToken);
        return link == null ? null : forks.FirstOrDefault(item =>
            SamePlaylistId(item.OriginalPlaylistId, PlaylistVirtualizationService.CreateProtocolId(link.Id)));
    }

    // The membership writer lease covers the read, edit and save. One row publishes the copy and mapping together.
    public async Task SaveForkAsync(ProtocolExecutionContext context, PlaylistForkState fork, CancellationToken cancellationToken)
    {
        if (context.Protocol != ProtocolKind.Jellyfin || context.IsReadOnly) throw new UnauthorizedAccessException();
        var owner = context.RequireActor().EffectiveUserId ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hash = Key(context, fork.OriginalPlaylistId);
        var version = await db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == owner &&
            item.ProviderId == "allstarr" && item.ResourceKind == ForkResourceKind && item.ExternalIdHash == hash &&
            item.Protocol == "jellyfin" && item.BackendInstanceId == context.BackendInstanceId &&
            item.BackendPrincipalId == context.VerifiedBackendPrincipalId)
            .Select(item => (int?)item.SnapshotVersion).MaxAsync(cancellationToken) ?? 0;
        var payload = JsonSerializer.Serialize(fork);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            Protocol = "jellyfin",
            ProviderId = "allstarr",
            ResourceKind = ForkResourceKind,
            ExternalIdHash = hash,
            SnapshotVersion = version + 1,
            ProviderRevision = fork.Playlist.SourceRevision,
            RetrievedAt = DateTimeOffset.UtcNow,
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            CorrelationId = context.CorrelationId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool SamePlaylistId(string left, string right) =>
        left.Equals(right, StringComparison.Ordinal) || SameNativeId(left, right) ||
        PlaylistVirtualizationService.TryParseProtocolId(left, out var leftLink) &&
        PlaylistVirtualizationService.TryParseProtocolId(right, out var rightLink) && leftLink == rightLink;
}
