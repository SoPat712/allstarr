using allstarr.Core.Operations;

namespace allstarr.Services.Admin;

internal static class ActivityPresentation
{
    public static bool IsRoutine(string label, string state, string severity, string? action = null)
    {
        var normalized = state.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        if (severity.Equals("error", StringComparison.OrdinalIgnoreCase) || severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
            normalized is "failed" or "rejected" or "unhealthy" or "unresolved" or "error" or "blocked" or "unauthorized") return false;
        return normalized is "pending" or "waiting" or "deferred" or "retryscheduled" ||
            action is "track-match.rematch" or "automatic.review" or "recording.odesli.enrich" ||
            System.Text.RegularExpressions.Regex.IsMatch(label,
                @"background.?task|runtime settings source use|record[\s_-]+(?:a[\s_-]+)?listen|playback\.signal|track.?identity|canonical.?recording|history.?resolution",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

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
        "lyrics" => "Lyrics",
        "track-match" => "Matching",
        "recommendation" => "Recommendations",
        "health" => "Health",
        "storage" => "Backups",
        _ => "System"
    };
}
