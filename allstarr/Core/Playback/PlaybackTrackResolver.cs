using allstarr.Core.Storage;
using allstarr.Core.Protocols;
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

public sealed class PlaybackTrackResolver(IDbContextFactory<AllstarrDbContext> factory)
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
        return track == null
            ? null
            : new PlaybackTrackSnapshot(
                track.Id,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds);
    }
}
