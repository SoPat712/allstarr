namespace allstarr.Core.Providers.Spotify;

public static class SpotifyUserReference
{
    public static bool TryNormalize(string reference, out string id)
    {
        id = reference?.Trim() ?? string.Empty;
        if (id.StartsWith("spotify:user:", StringComparison.OrdinalIgnoreCase))
            id = id["spotify:user:".Length..];
        else if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "open.spotify.com" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 || segments[0] != "user") return false;
            id = segments[1];
        }
        return id.Length is > 0 and <= 200 && id.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
