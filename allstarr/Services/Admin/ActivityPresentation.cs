using allstarr.Core.Operations;

namespace allstarr.Services.Admin;

internal static class ActivityPresentation
{
    public static string? SafeText(string? value, int maxLength = 1000)
    {
        var text = SafeOperationalText.Sanitize(value, maxLength);
        if (string.IsNullOrWhiteSpace(text) || Guid.TryParse(text, out _)) return null;
        var compact = text.Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Length >= 32 && compact.All(Uri.IsHexDigit) ? null : text;
    }

    public static string UserName(string? name) =>
        string.IsNullOrWhiteSpace(name) || Guid.TryParse(name, out _)
            ? "Unknown user" : SafeOperationalText.Sanitize(name, 200) ?? "Unknown user";

    public static string LibraryName(string? backend) => backend?.ToLowerInvariant() switch
    {
        "jellyfin" => "Jellyfin library",
        "navidrome" => "Navidrome library",
        "subsonic" or "opensubsonic" => "Subsonic library",
        _ => "Local library"
    };

    public static string JobSource(string type, string? backend) => type.Split('.')[0] switch
    {
        "library" => LibraryName(backend),
        "playlist" => "Playlists",
        "playback" => "Listening",
        "favorite" => "Favorites",
        "spotify-likes" => "Spotify",
        "recording" => "Recordings",
        "track-match" => "Matching",
        "recommendation" => "Recommendations",
        "health" => "Health",
        "storage" => "Backups",
        _ => "System"
    };
}
