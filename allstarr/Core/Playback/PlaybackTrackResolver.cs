using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed record PlaybackTrackSnapshot(
    Guid LibraryTrackId,
    string BackendItemId,
    string Title,
    string Artist,
    string? Album,
    long DurationMilliseconds);

public interface IPlaybackTrackResolver
{
    Task<PlaybackTrackSnapshot?> ResolveAsync(
        PlaybackSignalPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed class PlaybackTrackResolver(
    IDbContextFactory<AllstarrDbContext> factory,
    IEnumerable<IPlaybackMetadataResolver>? metadataResolvers = null)
    : IPlaybackTrackResolver
{
    public async Task<PlaybackTrackSnapshot?> ResolveAsync(
        PlaybackSignalPayload payload,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var itemId = payload.ItemId.StartsWith("backend:", StringComparison.Ordinal)
            ? payload.ItemId[8..]
            : payload.ItemId;
        var tracks = await db.LibraryTracks.AsNoTracking().Where(track =>
            track.TenantId == payload.Scope.TenantId &&
            track.OwnerUserId == payload.Scope.OwnerUserId &&
            track.Protocol == payload.Scope.Protocol &&
            track.BackendInstanceId == payload.Scope.BackendInstanceId &&
            track.LibraryScopeId == payload.Scope.LibraryScopeId).ToListAsync(cancellationToken);
        var track = tracks.SingleOrDefault(candidate =>
            ProtocolLibraryScopeResolver.Matches(candidate, itemId));
        if (track != null)
        {
            return new PlaybackTrackSnapshot(
                track.Id,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds);
        }

        foreach (var resolver in metadataResolvers ?? [])
        {
            var metadata = await resolver.ResolveAsync(itemId, cancellationToken);
            if (metadata != null && metadata.DurationSeconds > 0)
            {
                return new PlaybackTrackSnapshot(
                    Guid.Empty,
                    itemId,
                    metadata.Title,
                    metadata.Artist,
                    metadata.Album,
                    metadata.DurationSeconds.Value * 1000L);
            }
        }

        return null;
    }
}
