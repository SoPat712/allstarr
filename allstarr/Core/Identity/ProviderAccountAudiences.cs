using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Identity;

public static class ProviderAccountAudiences
{
    public static ProviderAccountScope[] Supported(IProviderRegistry? registry, string providerId)
    {
        var capabilities = registry != null && registry.TryGet(providerId, out var provider)
            ? provider!.Capabilities.Where(item => item.AccountRequirement != ProviderAccountRequirement.None).ToArray()
            : [];
        return Enum.GetValues<ProviderAccountScope>()
            .Where(scope => capabilities.All(item => item.AllowedAccountScopes.Contains(scope))).ToArray();
    }

    public static string Description(IProviderRegistry? registry, string providerId)
    {
        if (providerId is "lastfm" or "listenbrainz" or "multi-scrobbler" or "koito")
            return "Plays from listeners without their own account are sent here.";
        var capabilities = registry != null && registry.TryGet(providerId, out var provider)
            ? provider!.Capabilities.Select(item => item.Capability).ToArray() : [];
        var descriptions = new List<string>();
        if (capabilities.Contains(ProviderCapabilityKind.Streaming) || capabilities.Contains(ProviderCapabilityKind.Download))
            descriptions.Add("Everyone in the household can play songs from this account.");
        if (capabilities.Contains(ProviderCapabilityKind.Playlist))
            descriptions.Add("Everyone can import and sync playlists from this account.");
        if (capabilities.Contains(ProviderCapabilityKind.Metadata) || capabilities.Contains(ProviderCapabilityKind.Lyrics))
            descriptions.Add("Everyone can use this account for song details and lyrics.");
        return descriptions.Count > 0 ? string.Join(" ", descriptions)
            : "Everyone in the household can use this account. Personal accounts take priority.";
    }

}
