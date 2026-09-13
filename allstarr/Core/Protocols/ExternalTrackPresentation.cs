using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

public static class ExternalTrackPresentation
{
    public static string Title(Song song) => song.IsLocal
        ? song.Title
        : $"{song.Title} {(song.ExplicitContentLyrics == 1 ? "[A]/[E]" : "[A]")}";

    public const string PlaybackDescription =
        "Injected by Allstarr. Playback uses an available verified source; the catalog provider is not necessarily the streaming provider.";
}
