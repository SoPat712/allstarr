using allstarr.Models.Admin;
using allstarr.Models.Spotify;
using allstarr.Services.Spotify;

namespace allstarr.Services.Common;

/// <summary>
/// Rebinds a restored manual decision only when the same Spotify identity already
/// has a playable target. It never performs title-based or fuzzy migration.
/// </summary>
public static class LegacyManualMappingRecovery
{
    public static bool TryCreateReplacement(
        ManualMappingEntry legacy,
        ManualMappingEntry? compatiblePeer,
        SpotifyTrackMapping? canonical,
        out ManualMappingEntry replacement)
    {
        replacement = legacy;
        if (string.IsNullOrWhiteSpace(legacy.SpotifyId) ||
            string.IsNullOrWhiteSpace(legacy.ExternalProvider) ||
            string.IsNullOrWhiteSpace(legacy.ExternalId) ||
            ExternalTrackPlaybackPolicy.CanUseForPlayback(legacy.ExternalProvider))
        {
            return false;
        }

        if (IsPlayable(compatiblePeer) &&
            string.Equals(legacy.SpotifyId, compatiblePeer!.SpotifyId, StringComparison.OrdinalIgnoreCase))
        {
            replacement = CopyTarget(legacy, compatiblePeer);
            return true;
        }

        if (canonical == null ||
            !string.Equals(legacy.SpotifyId, canonical.SpotifyId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(canonical.TargetType, "local", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(canonical.LocalId))
        {
            replacement = new ManualMappingEntry
            {
                SpotifyId = legacy.SpotifyId,
                JellyfinId = canonical.LocalId,
                CreatedAt = legacy.CreatedAt
            };
            return true;
        }

        var target = canonical.ExternalMappings.FirstOrDefault(mapping =>
            !string.IsNullOrWhiteSpace(mapping.ExternalId) &&
            ExternalTrackPlaybackPolicy.CanUseForPlayback(mapping.Provider));
        var provider = target?.Provider ?? canonical.ExternalProvider;
        var externalId = target?.ExternalId ?? canonical.ExternalId;
        if (!string.IsNullOrWhiteSpace(externalId) &&
            ExternalTrackPlaybackPolicy.CanUseForPlayback(provider))
        {
            replacement = new ManualMappingEntry
            {
                SpotifyId = legacy.SpotifyId,
                ExternalProvider = provider,
                ExternalId = externalId,
                CreatedAt = legacy.CreatedAt
            };
            return true;
        }

        return false;
    }

    private static bool IsPlayable(ManualMappingEntry? mapping) =>
        mapping != null &&
        (!string.IsNullOrWhiteSpace(mapping.JellyfinId) ||
         (!string.IsNullOrWhiteSpace(mapping.ExternalId) &&
          ExternalTrackPlaybackPolicy.CanUseForPlayback(mapping.ExternalProvider)));

    private static ManualMappingEntry CopyTarget(ManualMappingEntry legacy, ManualMappingEntry target) => new()
    {
        SpotifyId = legacy.SpotifyId,
        JellyfinId = target.JellyfinId,
        ExternalProvider = target.ExternalProvider,
        ExternalId = target.ExternalId,
        CreatedAt = legacy.CreatedAt
    };
}
