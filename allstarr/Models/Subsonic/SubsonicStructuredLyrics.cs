namespace allstarr.Models.Subsonic;

public sealed record SubsonicLyricLine(long StartMilliseconds, string Text);

public sealed record SubsonicStructuredLyrics(
    string DisplayArtist,
    string DisplayTitle,
    string Language,
    long OffsetMilliseconds,
    bool Synced,
    IReadOnlyList<SubsonicLyricLine> Lines);
