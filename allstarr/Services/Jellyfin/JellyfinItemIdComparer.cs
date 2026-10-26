using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Services.Local;

namespace allstarr.Services.Jellyfin;

internal sealed class JellyfinItemIdComparer : IEqualityComparer<string?>, IComparer<string?>
{
    public static readonly JellyfinItemIdComparer Instance = new();

    private JellyfinItemIdComparer() { }

    public bool Equals(string? left, string? right) => string.Equals(IdentityKey(left), IdentityKey(right), StringComparison.Ordinal);

    public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(IdentityKey(value));

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        return StringComparer.Ordinal.Compare(SortKey(left), SortKey(right));
    }

    public static bool IsExternalId(string? id) => id != null && LocalLibraryService.ParseExternalResource(id).isExternal;

    public static string IdentityKey(string? id)
    {
        if (id == null) return "null";
        var parsed = LocalLibraryService.ParseExternalResource(id);
        if (parsed.isExternal)
            return ExternalResourceKey(parsed.provider!, parsed.type!, parsed.externalId!);
        return Guid.TryParse(id, out var guid)
            ? $"native-guid:{guid:N}"
            : $"native-value:{Part(id)}";
    }

    public static string ExternalResourceKey(string provider, string resourceKind, string externalId) =>
        $"external:{Part(NormalizeProvider(provider))}:{Part(NormalizeResourceKind(resourceKind))}:{Part(externalId)}";

    public static string MetadataKey(string itemType, string providerName, string value)
    {
        var canonicalProvider = NormalizeProvider(providerName);
        var canonicalValue = value;
        if (canonicalProvider == "isrc") canonicalValue = value.ToUpperInvariant();
        else if ((canonicalProvider is "musicbrainz" or "musicbrainztrack" or "musicbrainzrecording" or
                 "musicbrainzartist" or "musicbrainzalbum" or "musicbrainzalbumartist" or
                 "musicbrainzrelease" or "musicbrainzreleasegroup") && Guid.TryParse(value, out var guid))
            canonicalValue = guid.ToString("N");
        return $"metadata:{Part(NormalizeResourceKind(itemType))}:{Part(canonicalProvider)}:{Part(canonicalValue)}";
    }

    public static string NormalizeProvider(string provider) => AppleMusicClient.NormalizeProviderId(provider);

    private static string SortKey(string id)
    {
        var parsed = LocalLibraryService.ParseExternalResource(id);
        if (parsed.isExternal)
            return $"ext-{NormalizeProvider(parsed.provider!)}-{parsed.type}-{parsed.externalId}";
        return Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;
    }

    private static string NormalizeResourceKind(string resourceKind) => resourceKind.Trim().ToLowerInvariant() switch
    {
        "audio" or "song" => "song",
        "musicalbum" or "album" => "album",
        "musicartist" or "artist" => "artist",
        var value => value
    };

    private static string Part(string value) => $"{value.Length}:{value}";
}
