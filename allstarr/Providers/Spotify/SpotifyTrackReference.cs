namespace allstarr.Core.Providers.Spotify;

public static class SpotifyTrackReference
{
    public static bool TryNormalize(string? reference, out string id)
    {
        id = reference?.Trim() ?? string.Empty;
        if (id.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            id = id["spotify:track:".Length..];
        else if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
        {
            var rawPath = id.Split('?', '#')[0];
            if (rawPath.Contains('%') || rawPath.Contains('\\') || rawPath.Contains("/../", StringComparison.Ordinal) || rawPath.Contains("/./", StringComparison.Ordinal)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "open.spotify.com" ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath.Contains('%') ||
                uri.AbsolutePath.Contains('\\')) return false;
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0].StartsWith("intl-", StringComparison.Ordinal) &&
                parts[0].Length is >= 7 and <= 12 && parts[0][5..].All(character => char.IsAsciiLetter(character) || character == '-'))
                parts = parts[1..];
            if (parts.Length != 2 || parts[0] != "track" || uri.AbsolutePath.EndsWith('/')) return false;
            id = parts[1];
        }
        if (id.Length == 22 && id.All(char.IsAsciiLetterOrDigit)) return true;
        id = string.Empty;
        return false;
    }
}
