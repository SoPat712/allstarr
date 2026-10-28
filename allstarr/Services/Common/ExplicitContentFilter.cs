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
        => song.IsLocal && filter != ExplicitFilter.CleanOnly || ShouldInclude(song.ExplicitContentLyrics, filter);

    public static bool ShouldInclude(int? explicitContent, ExplicitFilter filter)
    {
        // Unrated content needs a mode that does not require clean evidence.
        if (explicitContent == null)
            return filter != ExplicitFilter.CleanOnly;

        return filter switch
        {
            // All: No filtering, include everything
            ExplicitFilter.All => true,

            // Hide edited versions; naturally clean and unrated songs remain visible.
            ExplicitFilter.ExplicitOnly => explicitContent != 3,

            // CleanOnly: Only show clean content
            // Include: 0 (naturally clean), 3 (clean/edited version)
            // Withhold explicit, unrated and unrecognized values.
            ExplicitFilter.CleanOnly => explicitContent is 0 or 3,

            _ => true
        };
    }
}
