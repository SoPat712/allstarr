using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace allstarr.Core.Playback;

public sealed record PlaybackTrackSnapshot(
    Guid? LibraryTrackId,
    Guid? CanonicalRecordingId,
    string BackendItemId,
    string Title,
    string Artist,
    string? Album,
    long? DurationMilliseconds,
    string? ProviderId = null,
    Guid? ProviderAccountId = null,
    Guid? ProviderTrackIdentityId = null,
    string? ProviderTrackReference = null);

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
                track.CanonicalRecordingId,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds);
        }

        foreach (var resolver in metadataResolvers ?? [])
        {
            var metadata = await resolver.ResolveAsync(itemId, cancellationToken);
            if (metadata != null)
            {
                var external = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
                ProviderTrackIdentityRecord? identity = null;
                if (external != null)
                {
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(external.Value.ExternalId)));
                    var candidates = await db.ProviderTrackIdentities.AsNoTracking()
                        .Where(candidate => candidate.TenantId == payload.Scope.TenantId && candidate.ExternalIdHash == hash)
                        .ToListAsync(cancellationToken);
                    var matching = candidates.Where(candidate => candidate.ProviderId.Equals(external.Value.Provider, StringComparison.OrdinalIgnoreCase)).ToList();
                    identity = matching.FirstOrDefault(candidate => candidate.ProviderAccountId == null) ??
                               (matching.Count == 1 ? matching[0] : null);
                }
                return new PlaybackTrackSnapshot(
                    null,
                    identity?.CanonicalRecordingId,
                    itemId,
                    metadata.Title,
                    metadata.Artist,
                    metadata.Album,
                    metadata.DurationSeconds is > 0 ? metadata.DurationSeconds.Value * 1000L : null,
                    external?.Provider,
                    identity?.ProviderAccountId,
                    identity?.Id,
                    external == null ? null : $"{external.Value.Provider}:{external.Value.ExternalId}");
            }
        }

        return null;
    }
}
