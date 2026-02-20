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
    {
        // If no explicit content info, include the song
        if (song.ExplicitContentLyrics == null)
            return true;
        
        return filter switch
        {
            // All: No filtering, include everything
            ExplicitFilter.All => true,
            
            // ExplicitOnly: Exclude clean/edited versions (value 3)
            // Include: 0 (naturally clean), 1 (explicit), 2 (not applicable), 6/7 (unknown)
            ExplicitFilter.ExplicitOnly => song.ExplicitContentLyrics != 3,
            
            // CleanOnly: Only show clean content
            // Include: 0 (naturally clean), 3 (clean/edited version)
            // Exclude: 1 (explicit)
            ExplicitFilter.CleanOnly => song.ExplicitContentLyrics != 1,
            
            _ => true
        };
    }
}
