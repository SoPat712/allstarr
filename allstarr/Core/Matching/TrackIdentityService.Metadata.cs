using System.Text.Json;
using allstarr.Services.Local;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task<string?> GetStoredMetadataAsync(ProviderActorContext actor, ProviderExternalResourceId resource,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var kind = DisplayResourceKind(resource);
        var hash = CanonicalCatalogKeys.Hash(resource.Value);
        var userId = actor.EffectiveUserId;
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            item.ProviderId == resource.ProviderId && item.ResourceKind == kind && item.ExternalIdHash == hash &&
            (item.OwnerUserId == userId || userId == null && item.ProviderAccountId == null) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == userId))))
            .ToArrayAsync(cancellationToken);
        return snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
            .ThenBy(item => item.Id).FirstOrDefault()?.PayloadJson;
    }

    public async Task<Uri?> GetStoredArtworkUriAsync(ProviderExternalResourceId resource,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var kind = resource.ResourceKind == ProviderResourceKind.Track ? "track" : DisplayResourceKind(resource);
        var query = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.ResourceKind == kind ||
            kind == "track" && item.ResourceKind == "playback-metadata");
        if (resource.ProviderId == "allstarr")
        {
            if (!Guid.TryParse(resource.Value, out var recordingId)) return null;
            // A merged recording's links moved to the recording that won the merge.
            for (var hops = 0; hops < 8 &&
                 await db.CanonicalRecordings.AsNoTracking().Where(item => item.Id == recordingId)
                     .Select(item => item.MergedIntoId).FirstOrDefaultAsync(cancellationToken) is { } winner; hops++)
                recordingId = winner;
            query = query.Where(item => db.ProviderTrackIdentities.Any(link =>
                link.CanonicalRecordingId == recordingId && link.Verification == ProviderIdentityVerification.Verified &&
                link.ProviderId == item.ProviderId && link.ExternalIdHash == item.ExternalIdHash));
        }
        else
        {
            var hash = CanonicalCatalogKeys.Hash(resource.Value);
            query = query.Where(item => item.ProviderId == resource.ProviderId && item.ExternalIdHash == hash);
        }
        var snapshots = await query.ToArrayAsync(cancellationToken);
        foreach (var snapshot in snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion))
        {
            try
            {
                using var document = JsonDocument.Parse(snapshot.PayloadJson);
                static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
                    ? value.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
                static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var root = document.RootElement;
                var cover = Text(Property(root, kind == "artist" ? "ImageUrl" : "CoverArtUrl")) ??
                    Text(Property(root, "ArtworkUrl")) ?? Text(Property(Property(root, "Artwork"), "PublicUri"));
                if (Uri.TryCreate(cover, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri;
                if (kind != "track") continue;
                var album = LocalLibraryService.ParseExternalResource(ReadDisplaySong(snapshot.PayloadJson, snapshot.ProviderId, resource.Value)?.AlbumId ?? "");
                if (!album.isExternal || album.type != "album") continue;
                var provider = album.provider is "apple-download" or "applemusic" ? "apple-musickit" : album.provider!;
                if (await GetStoredArtworkUriAsync(new(provider, ProviderResourceKind.Album, album.externalId!), cancellationToken) is { } stored) return stored;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string DisplayResourceKind(ProviderExternalResourceId resource) => resource.ResourceKind switch
    {
        ProviderResourceKind.Album => "album",
        ProviderResourceKind.Artist => "artist",
        _ => throw new ArgumentException("Stored resource metadata requires an album or artist.", nameof(resource))
    };
}
