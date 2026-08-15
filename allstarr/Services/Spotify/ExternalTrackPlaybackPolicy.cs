namespace allstarr.Services.Spotify;

using allstarr.Models.Domain;

/// <summary>
/// Keeps catalog-only providers out of mappings that are expected to stream or download audio.
/// </summary>
public static class ExternalTrackPlaybackPolicy
{
    public static bool CanUseForPlayback(Song? song)
    {
        if (song == null)
        {
            return false;
        }

        return song.IsLocal || CanUseForPlayback(song.ExternalProvider, song.Id);
    }

    public static bool CanUseForPlayback(string? provider, string? trackId = null)
    {
        var normalized = Normalize(provider);
        if (normalized == "tidal")
        {
            return false;
        }

        var normalizedTrackId = (trackId ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedTrackId.StartsWith("ext-tidal-", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Length > 0;
    }

    public static string Normalize(string? provider)
    {
        var normalized = (provider ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized == "applemusic" ? "appledownload" : normalized;
    }
}
