using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Core.Matching;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Playback;

public sealed record PlaybackTrackSnapshot(
    Guid? LibraryTrackId,
    Guid? CanonicalRecordingId,
    string BackendItemId,
    string Title,
    string Artist,
    string? Album,
    long? DurationMilliseconds,
    string? AlbumArtist = null,
    string? RecordingMusicBrainzId = null,
    int? TrackNumber = null,
    string? ProviderId = null,
    Guid? ProviderAccountId = null,
    Guid? ProviderTrackIdentityId = null,
    string? ProviderTrackReference = null,
    string? Isrc = null);

public interface IPlaybackTrackResolver
{
    Task<PlaybackTrackSnapshot?> ResolveAsync(
        PlaybackSignalPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed class PlaybackTrackResolver(
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess,
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
        var access = await libraryAccess.ResolveUserAsync(payload.Scope.OwnerUserId, cancellationToken);
        var tracks = await LibraryTrackAccess.Query(db, access).Where(track =>
            track.Protocol == payload.Scope.Protocol &&
            track.BackendInstanceId == payload.Scope.BackendInstanceId).ToListAsync(cancellationToken);
        var track = tracks.Where(candidate => Matches(candidate, itemId))
            .OrderBy(candidate => candidate.BackendLibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.BackendItemId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
        if (track != null)
        {
            return new PlaybackTrackSnapshot(
                track.Id,
                track.CanonicalRecordingId,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds,
                track.AlbumArtist,
                track.MusicBrainzRecordingId,
                Isrc: track.Isrc);
        }

        var viewer = access.Context;
        if (viewer != null && (viewer.Principal?.UserId != payload.Scope.OwnerUserId ||
                              viewer.BackendInstanceId != payload.Scope.BackendInstanceId ||
                              viewer.Protocol.ToString().ToLowerInvariant() != payload.Scope.Protocol))
            return null;
        foreach (var resolver in metadataResolvers ?? [])
        {
            var metadata = viewer == null
                ? await resolver.ResolveAsync(itemId, cancellationToken)
                : await resolver.ResolveAsync(itemId, viewer, cancellationToken);
            if (metadata != null)
            {
                var external = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
                var source = payload.StreamSource;
                if (source?.Matches(payload.Scope.Protocol, payload.Scope.BackendInstanceId) != true)
                    source = null;
                if (external != null && source != null)
                    external = (source.ProviderId, source.ExternalId);
                ProviderTrackIdentityRecord? identity = null;
                if (external != null)
                {
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(external.Value.ExternalId)));
                    var candidates = await db.ProviderTrackIdentities.AsNoTracking()
                        .Where(candidate => candidate.ExternalIdHash == hash)
                        .ToListAsync(cancellationToken);
                    var matching = candidates.Where(candidate => candidate.ProviderId.Equals(external.Value.Provider, StringComparison.OrdinalIgnoreCase)).ToList();
                    identity = matching.FirstOrDefault(candidate => source?.AccountId != null && candidate.ProviderAccountId == source.AccountId) ??
                               matching.FirstOrDefault(candidate => candidate.ProviderAccountId == null);
                }
                return new PlaybackTrackSnapshot(
                    null,
                    identity?.CanonicalRecordingId,
                    itemId,
                    metadata.Title,
                    metadata.Artist,
                    metadata.Album,
                    metadata.DurationSeconds is > 0 ? metadata.DurationSeconds.Value * 1000L : null,
                    metadata.AlbumArtist,
                    metadata.RecordingMusicBrainzId,
                    metadata.TrackNumber,
                    external?.Provider,
                    source?.AccountId ?? identity?.ProviderAccountId,
                    identity?.Id,
                    external == null ? null : $"{external.Value.Provider}:{external.Value.ExternalId}");
            }
        }

        return null;
    }
    internal static bool Matches(LibraryTrackRecord track, string itemId)
    {
        if (track.BackendItemId.Equals(itemId, StringComparison.Ordinal) ||
            track.Id.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
            track.CanonicalRecordingId?.ToString("D").Equals(itemId, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        try
        {
            var providers = JsonSerializer.Deserialize<Dictionary<string, string>>(track.ProviderIdsJson);
            return providers?.Any(pair =>
                pair.Value.Equals(itemId, StringComparison.Ordinal) ||
                $"{pair.Key}:{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"external:{pair.Key}:{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-song-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase) ||
                $"ext-{pair.Key}-{pair.Value}".Equals(itemId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
