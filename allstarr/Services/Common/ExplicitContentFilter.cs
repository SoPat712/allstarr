using allstarr.Models.Domain;
using allstarr.Models.Settings;

namespace allstarr.Services.Common;

/// <summary>
/// Utility class for filtering songs based on explicit content settings.
/// Centralizes explicit content filtering logic used across metadata services.
/// </summary>
public static class ExplicitContentFilter
{
    /// <summary>
    /// Determines if a song should be included based on explicit content filter settings.
    /// </summary>
    /// <param name="song">The song to check</param>
    /// <param name="filter">The explicit content filter setting</param>
    /// <returns>True if the song should be included, false otherwise</returns>
    public static bool ShouldIncludeSong(Song song, ExplicitFilter filter)
        => song.IsLocal || ShouldInclude(song.ExplicitContentLyrics, filter);

    public static bool ShouldInclude(int? explicitContent, ExplicitFilter filter)
    {
        // If no explicit content info, include the song
        if (explicitContent == null)
            return true;

        return filter switch
        {
            // All: No filtering, include everything
            ExplicitFilter.All => true,

            // Unknown and unrated songs remain visible in every mode.
            ExplicitFilter.ExplicitOnly => explicitContent is not (0 or 3),

            // CleanOnly: Only show clean content
            // Include: 0 (naturally clean), 3 (clean/edited version)
            // Exclude: 1 (explicit)
            ExplicitFilter.CleanOnly => explicitContent != 1,

            _ => true
        };
    }
}
