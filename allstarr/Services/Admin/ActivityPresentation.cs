using allstarr.Core.Operations;

namespace allstarr.Services.Admin;

internal static class ActivityPresentation
{
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
        "recording" => "Recordings",
        "track-match" => "Matching",
        "recommendation" => "Recommendations",
        _ => "System"
    };
}
