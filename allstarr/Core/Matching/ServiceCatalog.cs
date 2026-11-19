using allstarr.Core.Artwork;
using allstarr.Core.Providers.AppleMusicKit;

namespace allstarr.Core.Matching;

/// <summary>
/// Sources that read one service's catalogue share its ids: an artist id from the Apple Music extension names the same
/// artist as that id from the built-in Apple Music source. That is proof on its own, with no names involved.
/// </summary>
internal static class ServiceCatalog
{
    /// <summary>The service a source reads from, such as "deezer" for both Deezer sources.</summary>
    internal static string Of(string providerId) =>
        ProviderArtworkResolver.Brand(AppleMusicClient.NormalizeProviderId(providerId));

    /// <summary>One key for an id whichever of a service's sources gave it.</summary>
    internal static string Key(string providerId, string id)
    {
        var service = Of(providerId);
        // Some extensions spell their ids "deezer:123".
        var prefix = service + ":";
        return prefix + (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id);
    }

    internal static bool Same(string providerId, string id, string otherProviderId, string otherId) =>
        string.Equals(Key(providerId, id), Key(otherProviderId, otherId), StringComparison.Ordinal);
}
