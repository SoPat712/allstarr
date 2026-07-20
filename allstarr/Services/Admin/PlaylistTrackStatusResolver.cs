using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Spotify;

namespace allstarr.Services.Admin;

/// <summary>
/// Resolves track status (local/external/missing) from ordered Spotify matched-track cache entries.
/// </summary>
public static class PlaylistTrackStatusResolver
{
    /// <summary>
    /// Reconciles a provider snapshot entry with an item that is already present in the
    /// materialized backend playlist. Provider titles frequently retain a featuring
    /// decorator while Jellyfin stores the same recording without it, so use the same
    /// decorator-insensitive identity rule as the local matcher.
    /// </summary>
    public static bool MaterializedIdentityMatches(
        string? sourceTitle,
        string? sourcePrimaryArtist,
        string? materializedTitle,
        IEnumerable<string>? materializedArtists)
    {
        var normalizedSourceTitle = NormalizeIdentity(FuzzyMatcher.StripDecorators(sourceTitle ?? string.Empty));
        var normalizedMaterializedTitle = NormalizeIdentity(FuzzyMatcher.StripDecorators(materializedTitle ?? string.Empty));
        if (normalizedSourceTitle.Length == 0 ||
            !normalizedSourceTitle.Equals(normalizedMaterializedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedSourceArtist = NormalizeIdentity(sourcePrimaryArtist);
        var normalizedMaterializedArtists = (materializedArtists ?? [])
            .Select(NormalizeIdentity)
            .Where(artist => artist.Length > 0)
            .ToArray();

        return normalizedSourceArtist.Length == 0 ||
               normalizedMaterializedArtists.Length == 0 ||
               normalizedMaterializedArtists.Contains(normalizedSourceArtist, StringComparer.Ordinal);
    }

    public static bool TryResolveFromMatchedTrack(
        IReadOnlyDictionary<string, MatchedTrack> matchedTracksBySpotifyId,
        string? spotifyId,
        out bool? isLocal,
        out string? externalProvider)
    {
        isLocal = null;
        externalProvider = null;

        if (matchedTracksBySpotifyId == null || matchedTracksBySpotifyId.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return false;
        }

        if (!matchedTracksBySpotifyId.TryGetValue(spotifyId, out var matched) ||
            matched?.MatchedSong == null)
        {
            return false;
        }

        var matchType = matched.MatchType ?? string.Empty;
        var isExplicitLocalMatch = matchType.Contains("local", StringComparison.OrdinalIgnoreCase);
        var isExplicitExternalMatch = matchType.Contains("external", StringComparison.OrdinalIgnoreCase);
        var providerFromSong = NormalizeExternalProvider(matched.MatchedSong.ExternalProvider)
            ?? ExtractExternalProviderFromItemId(matched.MatchedSong.Id);

        if (!string.IsNullOrWhiteSpace(providerFromSong) &&
            !ExternalTrackPlaybackPolicy.CanUseForPlayback(providerFromSong, matched.MatchedSong.Id))
        {
            return false;
        }

        // If we have an explicit external signature (provider or ext- ID prefix),
        // trust that over a stale/incorrect local match type.
        if (!string.IsNullOrWhiteSpace(providerFromSong))
        {
            isLocal = false;
            externalProvider = providerFromSong;
            return true;
        }

        if (isExplicitLocalMatch)
        {
            isLocal = true;
            externalProvider = null;
            return true;
        }

        isLocal = isExplicitExternalMatch ? false : matched.MatchedSong.IsLocal;

        if (isLocal == false)
        {
            externalProvider = providerFromSong;
        }

        return true;
    }

    private static string? NormalizeExternalProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "squidwtf" or "squid-wtf" or "squid_wtf" or "tidal" => "squidwtf",
            "deezer" => "deezer",
            "qobuz" => "qobuz",
            var other => other
        };
    }

    private static string? ExtractExternalProviderFromItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var trimmed = itemId.Trim();
        if (!trimmed.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = trimmed.Split('-', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return NormalizeExternalProvider(parts[1]);
    }

    private static string NormalizeIdentity(string? value) => string.Concat(
        (value ?? string.Empty).Normalize().ToLowerInvariant().Where(char.IsLetterOrDigit));
}
