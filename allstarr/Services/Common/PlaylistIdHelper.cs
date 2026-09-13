namespace allstarr.Services.Common;

public static class PlaylistIdHelper
{
    private const string Prefix = "ext-";
    private const string Separator = "-playlist-";
    private const string PlaylistType = "playlist";

    public static bool IsExternalPlaylist(string? id) =>
        id?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true &&
        id.Contains(Separator, StringComparison.OrdinalIgnoreCase);

    public static (string provider, string externalId) ParsePlaylistId(string id)
    {
        if (!IsExternalPlaylist(id))
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'ext-{{provider}}-playlist-{{externalId}}', got '{id}'", nameof(id));
        }

        var withoutPrefix = id[Prefix.Length..];
        var playlistIndex = withoutPrefix.IndexOf(Separator, StringComparison.OrdinalIgnoreCase);
        if (playlistIndex == -1)
        {
            throw new ArgumentException($"Invalid playlist ID format. Expected 'ext-{{provider}}-playlist-{{externalId}}', got '{id}'", nameof(id));
        }

        var provider = withoutPrefix[..playlistIndex];
        var externalId = withoutPrefix[(playlistIndex + Separator.Length)..];

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            throw new ArgumentException($"Invalid playlist ID format. Provider or external ID is empty in '{id}'", nameof(id));
        }

        return (provider, externalId);
    }

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
