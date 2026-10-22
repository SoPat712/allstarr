using allstarr.Models.Domain;
using allstarr.Core.Settings;
using allstarr.Core.Playlists;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Core.Protocols;

public static class ExternalTrackPresentation
{
    public static string VirtualPath(Song song, string? container = null)
    {
        static string Segment(string value) => allstarr.Services.Common.PathHelper.SanitizeFileName(value);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(song.Id))).ToLowerInvariant()[..12];
        var number = song.Track is > 0 ? song.DiscNumber is > 0
            ? $"{song.DiscNumber:00}-{song.Track:00} " : $"{song.Track:00} " : string.Empty;
        var extension = container ?? song.Container;
        var suffix = extension is "flac" or "mp3" or "m4a" or "mp4" or "aac" or "ogg" or "opus" or "wav" or "aif" or "aiff" or "wma"
            ? "." + extension : string.Empty;
        return $"/music/external/{Segment(song.AlbumArtist ?? song.Artist)}/{Segment(song.Album)}/{number}{Segment(StripBestVersionPrefix(song.Title))} [{identity}]{suffix}";
    }

    public static ListeningPreferences Preferences(HttpContext? context) =>
        context?.GetProtocolExecutionContext()?.Policy?.Preferences ?? new();

    public static string Title(Song song, ListeningPreferences? preferences = null) => song.IsLocal
        ? song.Title
        : Title(BestVersionTitle(song.Title, song.ExternalProvider, song.IsBestVersion),
            song.ExplicitContentLyrics == 1, preferences);

    public static string StripBestVersionPrefix(string title) => allstarr.Services.Common.FuzzyMatcher.StripBestVersionPrefix(title);

    private static string BestVersionTitle(string title, string? provider, bool bestVersion) =>
        bestVersion && string.Equals(provider, "artistgrid", StringComparison.OrdinalIgnoreCase)
            ? $"⭐ {StripBestVersionPrefix(title)}"
            : title;

    public static string Title(string title, bool isExplicit, ListeningPreferences? preferences = null)
    {
        preferences ??= new();
        var external = preferences.ShowExternalLabel;
        var explicitLabel = preferences.ShowExplicitLabel && isExplicit;
        var label = (external, explicitLabel) switch
        {
            (true, true) => "[A]/[E]",
            (true, false) => "[A]",
            (false, true) => "[E]",
            _ => null
        };
        return label == null ? title : $"{title} {label}";
    }

    public static VirtualPlaylistReadModel ApplyPreferences(VirtualPlaylistReadModel playlist,
        EffectiveProviderPolicySnapshot? policy) => policy == null ? playlist : playlist with
        {
            Tracks = playlist.Tracks.Where(track =>
                playlist.ProjectionMode != PlaylistProjectionMode.Source && track.RouteKind == TrackRouteKind.Local ||
                policy.Includes(track.SourceMetadata?.IsExplicit is { } value ? value ? 1 : 0 : null)).ToArray()
        };

    public const string PlaybackDescription =
        "Injected by Allstarr. Playback uses an available verified source; the catalog provider is not necessarily the streaming provider.";
}
