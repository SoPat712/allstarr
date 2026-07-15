using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record FavoriteTrackEnrichmentMetadata(
    ManagedTrackPathValues Track,
    LocalMetadataSnapshot Local,
    MusicBrainzEnrichmentSnapshot? MusicBrainz,
    IReadOnlyList<ProviderMetadataSnapshot> Providers);

public sealed class FavoriteTrackMetadataResolver(IDbContextFactory<AllstarrDbContext> factory)
{
    public async Task<ManagedTrackPathValues?> ResolveAsync(FavoriteEventRecord favoriteEvent,
        CancellationToken cancellationToken) =>
        (await ResolveEnrichmentAsync(favoriteEvent, cancellationToken))?.Track;

    public async Task<FavoriteTrackEnrichmentMetadata?> ResolveEnrichmentAsync(
        FavoriteEventRecord favoriteEvent,
        CancellationToken cancellationToken)
    {
        var external = FavoriteMatchActionExecutor.ParseExternalTrack(favoriteEvent.ItemId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (external == null)
        {
            var local = await db.LibraryTracks.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
                item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
                item.LibraryScopeId == favoriteEvent.LibraryScopeId && item.BackendItemId == favoriteEvent.ItemId,
                cancellationToken);
            if (local == null) return null;
            var track = new ManagedTrackPathValues(local.Title, local.Artist, local.Album, local.AlbumArtist,
                Extension: Path.GetExtension(local.FilePath));
            var localSnapshot = new LocalMetadataSnapshot(
                new(local.Title), new(local.Artist), new(local.Album), new(local.AlbumArtist));
            var musicBrainz = HasMusicBrainzIdentity(
                    local.MusicBrainzRecordingId,
                    local.MusicBrainzReleaseId,
                    null,
                    local.MusicBrainzArtistId)
                ? new MusicBrainzEnrichmentSnapshot(
                    local.MusicBrainzRecordingId,
                    local.MusicBrainzReleaseId,
                    null,
                    local.MusicBrainzArtistId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
                : null;
            return new(track, localSnapshot, musicBrainz, []);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(external.Value.Id)));
        var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                item.TenantId == favoriteEvent.TenantId && item.OwnerUserId == favoriteEvent.OwnerUserId &&
                item.LibraryScopeId == favoriteEvent.LibraryScopeId && item.ProviderId == external.Value.Provider &&
                item.Protocol == favoriteEvent.Protocol && item.BackendInstanceId == favoriteEvent.BackendInstanceId &&
                item.BackendPrincipalId == favoriteEvent.BackendPrincipalId &&
                item.ResourceKind == "track" && item.ExternalIdHash == hash)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            var root = document.RootElement;
            var title = Text(root, "title", "Title");
            var artist = Text(root, "artist", "Artist", "artistName", "ArtistName");
            if (title == null || artist == null) return null;
            var album = Text(root, "album", "Album", "albumTitle", "AlbumTitle");
            var albumArtist = Text(root, "albumArtist", "AlbumArtist");
            var genre = Text(root, "genre", "Genre");
            var year = Integer(root, "year", "Year");
            var trackNumber = Integer(root, "track", "Track", "trackNumber", "TrackNumber");
            var track = new ManagedTrackPathValues(title, artist, album, albumArtist, genre, year,
                trackNumber, Extension: string.Empty);
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["title"] = title,
                ["artist"] = artist,
                ["album"] = album,
                ["albumArtist"] = albumArtist,
                ["genre"] = genre,
                ["year"] = year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["track"] = trackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var recordingId = Text(root, "musicBrainzRecordingId", "MusicBrainzRecordingId",
                "musicbrainz_recordingid", "mbid");
            var releaseId = Text(root, "musicBrainzReleaseId", "MusicBrainzReleaseId",
                "musicbrainz_releaseid");
            var releaseGroupId = Text(root, "musicBrainzReleaseGroupId", "MusicBrainzReleaseGroupId",
                "musicbrainz_releasegroupid");
            var artistId = Text(root, "musicBrainzArtistId", "MusicBrainzArtistId",
                "musicbrainz_artistid");
            var musicBrainz = HasMusicBrainzIdentity(recordingId, releaseId, releaseGroupId, artistId)
                ? new MusicBrainzEnrichmentSnapshot(recordingId, releaseId, releaseGroupId, artistId,
                    null, null, null, null, null, null, null)
                : null;
            return new(track,
                new LocalMetadataSnapshot(new(null), new(null)),
                musicBrainz,
                [new ProviderMetadataSnapshot(snapshot.ProviderId, snapshot.ProviderRevision, fields)]);
        }
        catch (JsonException) { return null; }
    }

    private static bool HasMusicBrainzIdentity(params string?[] ids) =>
        ids.Any(value => !string.IsNullOrWhiteSpace(value));

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        return null;
    }
    private static int? Integer(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
        return null;
    }
}
