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
        if (itemId.StartsWith("library:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(itemId[8..], out var libraryId))
            itemId = libraryId.ToString("D");
        var external = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
        var canonicalId = external is { Provider: "allstarr" } && Guid.TryParse(external.Value.ExternalId, out var recordingId)
            ? (Guid?)recordingId : null;
        var source = payload.StreamSource?.Matches(payload.Scope.Protocol, payload.Scope.BackendInstanceId) == true
            ? payload.StreamSource : null;
        var identityReference = source == null ? external : (source.ProviderId, source.ExternalId);
        ProviderTrackIdentityRecord? identity = null;
        if (identityReference != null)
        {
            var (provider, externalId) = identityReference.Value;
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)));
            var candidates = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(candidate => candidate.ExternalIdHash == hash).ToListAsync(cancellationToken);
            var matching = candidates.Where(candidate => candidate.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToList();
            identity = matching.FirstOrDefault(candidate => source?.AccountId != null && candidate.ProviderAccountId == source.AccountId) ??
                matching.FirstOrDefault(candidate => candidate.ProviderAccountId == null);
            canonicalId ??= identity?.CanonicalRecordingId;
        }
        var access = await libraryAccess.ResolveUserAsync(payload.Scope.OwnerUserId, cancellationToken);
        var tracks = await LibraryTrackAccess.Query(db, access).Where(track =>
            track.Protocol == payload.Scope.Protocol &&
            track.BackendInstanceId == payload.Scope.BackendInstanceId).ToListAsync(cancellationToken);
        var track = tracks.Where(candidate => Matches(candidate, itemId) || canonicalId != null && candidate.CanonicalRecordingId == canonicalId)
            .OrderBy(candidate => candidate.BackendLibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.BackendItemId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
        canonicalId ??= track?.CanonicalRecordingId;
        var canonicalDuration = canonicalId == null ? null : await db.CanonicalRecordings.AsNoTracking()
            .Where(recording => recording.Id == canonicalId).Select(recording => recording.DurationMilliseconds).SingleOrDefaultAsync(cancellationToken);
        PlaybackTrackSnapshot? partial = null;
        if (track != null)
        {
            partial = new PlaybackTrackSnapshot(
                track.Id,
                track.CanonicalRecordingId,
                track.BackendItemId,
                track.Title,
                track.Artist,
                track.Album,
                track.DurationMilliseconds is > 0 ? track.DurationMilliseconds : canonicalDuration,
                track.AlbumArtist,
                track.MusicBrainzRecordingId,
                Isrc: track.Isrc);
            if (partial.DurationMilliseconds is > 0 && !string.IsNullOrWhiteSpace(partial.Title) && !string.IsNullOrWhiteSpace(partial.Artist))
                return partial;
        }

        var viewer = access.Context;
        if (viewer != null && (viewer.Principal?.UserId != payload.Scope.OwnerUserId ||
                              viewer.BackendInstanceId != payload.Scope.BackendInstanceId ||
                              viewer.Protocol.ToString().ToLowerInvariant() != payload.Scope.Protocol))
            return null;
        var references = new[] { source == null ? null : $"ext-{source.ProviderId}-song-{source.ExternalId}", track?.BackendItemId, itemId }
            .OfType<string>().Distinct().ToArray();
        foreach (var reference in references)
        {
            foreach (var resolver in metadataResolvers ?? [])
            {
                PlaybackTrackMetadata? metadata;
                try
                {
                    metadata = viewer == null
                        ? await resolver.ResolveAsync(reference, cancellationToken)
                        : await resolver.ResolveAsync(reference, viewer, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { continue; }
                if (metadata != null)
                {
                    var resolved = new PlaybackTrackSnapshot(
                        track?.Id,
                        canonicalId,
                        itemId,
                        metadata.Title,
                        metadata.Artist,
                        metadata.Album,
                        metadata.DurationSeconds is > 0 ? metadata.DurationSeconds.Value * 1000L : canonicalDuration,
                        metadata.AlbumArtist,
                        metadata.RecordingMusicBrainzId,
                        metadata.TrackNumber,
                        identityReference?.Item1,
                        source?.AccountId ?? identity?.ProviderAccountId,
                        identity?.Id,
                        identityReference == null ? null : $"{identityReference.Value.Item1}:{identityReference.Value.Item2}");
                    if (resolved.DurationMilliseconds is > 0 && !string.IsNullOrWhiteSpace(resolved.Title) && !string.IsNullOrWhiteSpace(resolved.Artist))
                        return resolved;
                    partial ??= resolved;
                }
            }
        }

        return partial;
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
