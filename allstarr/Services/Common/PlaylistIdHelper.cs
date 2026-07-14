namespace allstarr.Services.Common;

/// <summary>
/// Helper class for handling external playlist IDs.
/// Playlist IDs use the format: "ext-{provider}-playlist-{externalId}"
/// Example: "ext-deezer-playlist-123456", "ext-qobuz-playlist-789"
/// This matches the format used for albums and songs for consistency.
/// </summary>
public static class PlaylistIdHelper
{
    private const string PlaylistType = "playlist";

    /// <summary>
    /// Checks if an ID represents an external playlist.
    /// Only supports new format: ext-{provider}-playlist-{id}
    /// </summary>
    /// <param name="id">The ID to check</param>
    /// <returns>True if the ID is a playlist ID in the new format, false otherwise</returns>
    public static bool IsExternalPlaylist(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        // New format only: ext-{provider}-playlist-{id}
        return id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) &&
               id.Contains("-playlist-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a playlist ID to extract provider and external ID.
    /// Only supports new format: ext-{provider}-playlist-{id}
    /// </summary>
    /// <param name="id">The playlist ID in format "ext-{provider}-playlist-{externalId}"</param>
    /// <returns>A tuple containing (provider, externalId)</returns>
    /// <exception cref="ArgumentException">Thrown if the ID format is invalid</exception>
    public static (string provider, string externalId) ParsePlaylistId(string id)
    {
        if (!IsExternalPlaylist(id))
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'ext-{{provider}}-playlist-{{externalId}}', got '{id}'", nameof(id));
        }

        // Format: ext-{provider}-playlist-{externalId}
        var withoutPrefix = id.Substring(4); // Remove "ext-"

        // Find "-playlist-" separator
        var playlistIndex = withoutPrefix.IndexOf("-playlist-", StringComparison.OrdinalIgnoreCase);
        if (playlistIndex == -1)
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'ext-{{provider}}-playlist-{{externalId}}', got '{id}'", nameof(id));
        }

        var provider = withoutPrefix.Substring(0, playlistIndex);
        var externalId = withoutPrefix.Substring(playlistIndex + 10); // 10 = length of "-playlist-"

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            throw new ArgumentException($"Invalid playlist ID format. Provider or external ID is empty in '{id}'", nameof(id));
        }

        return (provider, externalId);
    }

    /// <summary>
    /// Creates a playlist ID from provider and external ID.
    /// </summary>
    /// <param name="provider">The provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">The external ID from the provider</param>
    /// <returns>A playlist ID in format "ext-{provider}-playlist-{externalId}"</returns>
    public static string CreatePlaylistId(string provider, string externalId)
    {
        if (string.IsNullOrEmpty(provider))
        {
            throw new ArgumentException("Provider cannot be null or empty", nameof(provider));
        }

        if (string.IsNullOrEmpty(externalId))
        {
            throw new ArgumentException("External ID cannot be null or empty", nameof(externalId));
        }

        return $"ext-{provider.ToLowerInvariant()}-{PlaylistType}-{externalId}";
    }
}
