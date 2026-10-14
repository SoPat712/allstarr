namespace allstarr.Core.Providers.Spotify;

public static class SpotifyPlaylistReference
{
    public const string LikedSongsId = "liked-songs";

    public static bool TryNormalize(string reference, out string id)
    {
        id = reference?.Trim() ?? string.Empty;
        if (id == LikedSongsId) return true;
        if (id.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
            id = id["spotify:playlist:".Length..];
        else if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "open.spotify.com" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 3 && segments[0].StartsWith("intl-", StringComparison.Ordinal))
                segments = segments[1..];
            if (segments.Length != 2 || segments[0] != "playlist") return false;
            id = segments[1];
        }
        return id.Length is > 0 and <= 64 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
