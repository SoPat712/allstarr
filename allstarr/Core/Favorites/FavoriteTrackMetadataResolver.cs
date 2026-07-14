using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteTrackMetadataResolver(IDbContextFactory<AllstarrDbContext> factory)
{
    public async Task<ManagedTrackPathValues?> ResolveAsync(FavoriteEventRecord favoriteEvent,
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
            return local == null ? null : new(local.Title, local.Artist, local.Album, local.AlbumArtist,
                Extension: Path.GetExtension(local.FilePath));
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
            return new(title, artist, Text(root, "album", "Album", "albumTitle", "AlbumTitle"),
                Text(root, "albumArtist", "AlbumArtist"), Text(root, "genre", "Genre"),
                Integer(root, "year", "Year"), Integer(root, "track", "Track", "trackNumber", "TrackNumber"),
                Extension: string.Empty);
        }
        catch (JsonException) { return null; }
    }

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
