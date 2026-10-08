using allstarr.Models.Domain;
using allstarr.Core.Settings;
using allstarr.Core.Playlists;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Core.Protocols;

public static class ExternalTrackPresentation
{
    public static ListeningPreferences Preferences(HttpContext? context) =>
        context?.GetProtocolExecutionContext()?.Policy?.Preferences ?? new();

    public static string Title(Song song, ListeningPreferences? preferences = null) => song.IsLocal
        ? song.Title
        : Title(song.Title, song.ExplicitContentLyrics == 1, preferences);

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
