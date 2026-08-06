using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace allstarr.Services.Common;

/// <summary>
/// Utility class for building consistent cache keys across the application.
/// Centralizes cache key generation to ensure consistency and prevent typos.
/// </summary>
public static class CacheKeyBuilder
{
    public static string BuildPlaybackMetadataKey(string provider, string itemId) =>
        $"playback:metadata:v1:{Normalize(provider)}:{Digest(itemId.Trim())}";

    public static string BuildPlaybackMetadataNegativeKey(string provider, string itemId) =>
        $"negative:playback:metadata:v1:{Normalize(provider)}:{Digest(itemId.Trim())}";

    public static string BuildPlaybackRouteNegativeKey(
        Guid tenantId,
        Guid? userId,
        string? libraryScopeId,
        string provider,
        string itemId,
        string quality) =>
        $"negative:playback:route:v1:{DigestIdentity(
            tenantId, userId, libraryScopeId, provider, itemId, quality)}";

    public static string BuildJellyfinItemTypeKey(string itemId) =>
        $"jellyfin:item-type:v2:{Digest(itemId.Trim())}";

    public static string BuildPlaybackSignalDedupeKey(
        string signalType,
        string deviceId,
        string itemId) =>
        $"playback:signal:dedupe:v1:{DigestIdentity(signalType, deviceId, itemId)}";

    public static string BuildProviderPlaylistArtworkDescriptorKey(
        string provider,
        string playlistId,
        string? revision) =>
        $"playlist:artwork-descriptor:v1:{Normalize(provider)}:{Digest(playlistId.Trim())}:{Digest(revision?.Trim() ?? string.Empty)}";

    public static string BuildProviderPlaylistDiscoveryKey(
        Guid? tenantId,
        Guid? userId,
        Guid accountId,
        long accountRevision,
        string providerId,
        string? query,
        string? cursor,
        int limit)
    {
        return string.Join(':',
            "playlist",
            "discovery",
            "v2",
            tenantId?.ToString("N") ?? "global",
            userId?.ToString("N") ?? "shared",
            accountId.ToString("N"),
            accountRevision.ToString(CultureInfo.InvariantCulture),
            Normalize(providerId),
            DigestIdentity(query, cursor, limit));
    }

    public static string BuildProviderPlaylistDiscoveryAccountPattern(Guid accountId) =>
        $"playlist:discovery:v2:*:*:{accountId:N}:*";

    public static string BuildMediaAssetDescriptorKey(MediaAssetIdentity identity)
    {
        var resource = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity.ResourceId.Trim())));
        var revision = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity.Revision?.Trim() ?? string.Empty)));
        var dimensions = FormattableString.Invariant($"{identity.Width ?? 0}x{identity.Height ?? 0}");
        return string.Join(':',
            "media",
            "descriptor",
            "v3",
            identity.TenantId?.ToString("N") ?? "global",
            identity.UserId?.ToString("N") ?? "shared",
            identity.ProviderAccountId?.ToString("N") ?? "none",
            Normalize(identity.ProviderId),
            Normalize(identity.ResourceKind),
            resource,
            dimensions,
            revision);
    }

    public static string BuildMediaAssetDescriptorAccountPattern(Guid accountId) =>
        $"media:descriptor:v3:*:*:{accountId:N}:*";

    public static string BuildMediaAssetPayloadKey(string sha256) =>
        $"artwork:payload:v1:{Normalize(sha256)}";

    #region Search Keys

    public static string BuildSearchKey(
        string? searchTerm,
        string? itemTypes,
        int? limit,
        int? startIndex,
        string? parentId,
        string? sortBy,
        string? sortOrder,
        bool? recursive,
        string? userId,
        string? isFavorite = null)
    {
        return $"search:v2:{DigestIdentity(
            searchTerm,
            itemTypes,
            limit,
            startIndex,
            parentId,
            sortBy,
            sortOrder,
            recursive,
            userId,
            isFavorite)}";
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    #endregion

    #region Metadata Keys

    public static string BuildAlbumKey(string provider, string externalId)
    {
        return $"metadata:album:v1:{Normalize(provider)}:{Digest(externalId.Trim())}";
    }

    public static string BuildArtistKey(string provider, string externalId)
    {
        return $"metadata:artist:v1:{Normalize(provider)}:{Digest(externalId.Trim())}";
    }

    #endregion

    #region Lyrics Keys

    public static string BuildLyricsKey(string artist, string title, string? album, int? durationSeconds)
    {
        return $"lyrics:v2:{DigestIdentity(artist, title, album, durationSeconds)}";
    }

    public static string BuildLyricsByIdKey(int id)
    {
        return $"lyrics:id:v2:{id.ToString(CultureInfo.InvariantCulture)}";
    }

    #endregion

    #region Image Keys

    public static bool IsMediaAssetDescriptorKey(string key) =>
        key.StartsWith("media:descriptor:v3:", StringComparison.Ordinal);

    public static bool IsMediaAssetPayloadKey(string key) =>
        key.StartsWith("artwork:payload:v1:", StringComparison.Ordinal);

    #endregion

    #region Genre Keys

    public static string BuildGenreEnrichmentKey(string compositeCacheKey)
    {
        return $"genre:v2:{Digest(compositeCacheKey.Trim())}";
    }

    #endregion

    #region MusicBrainz Keys

    public static string BuildMusicBrainzIsrcKey(string isrc)
    {
        return $"musicbrainz:isrc:v2:{Normalize(isrc)}";
    }

    public static string BuildMusicBrainzSearchKey(string title, string artist, int limit)
    {
        return $"musicbrainz:search:v2:{DigestIdentity(title, artist, limit)}";
    }

    public static string BuildMusicBrainzMbidKey(string mbid)
    {
        return $"musicbrainz:mbid:v2:{Normalize(mbid)}";
    }

    public static string BuildMusicBrainzNegativeKey(string positiveKey) =>
        $"negative:{positiveKey}";

    #endregion

    #region Odesli Keys

    public static string BuildOdesliTidalToSpotifyKey(string tidalTrackId)
    {
        return $"odesli:tidal-to-spotify:v2:{Digest(tidalTrackId.Trim())}";
    }

    public static string BuildOdesliUrlToSpotifyKey(string musicUrl)
    {
        return $"odesli:url-to-spotify:v2:{HashOdesliUrl(musicUrl)}";
    }

    public static string BuildOdesliTranslationKey(string sourceUrl, string targetPlatform)
    {
        return $"odesli:translate:v2:{HashOdesliUrl(sourceUrl)}:{Normalize(targetPlatform)}";
    }

    private static string HashOdesliUrl(string value) =>
        Digest(value);

    private static string DigestIdentity(params object?[] values) =>
        Digest(string.Join('\u001f', values.Select(value => Normalize(
            value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value?.ToString()))));

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    #endregion
}
