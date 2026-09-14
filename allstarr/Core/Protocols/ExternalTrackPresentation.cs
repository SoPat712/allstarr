using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

public static class ExternalTrackPresentation
{
    public static string Title(Song song) => song.IsLocal
        ? song.Title
        : Title(song.Title, song.ExplicitContentLyrics == 1);

    public static string Title(string title, bool isExplicit) =>
        $"{title} {(isExplicit ? "[A]/[E]" : "[A]")}";

    public const string PlaybackDescription =
        "Injected by Allstarr. Playback uses an available verified source; the catalog provider is not necessarily the streaming provider.";
}
