using allstarr.Core.Capabilities;

namespace allstarr.Models.Lyrics;

public class LyricsInfo
{
    internal string? CacheProvenance { get; set; }
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    internal long? TimingCoverageMilliseconds { get; set; }
    public int Id { get; set; }
    public string TrackName { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public int Duration { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
    public string? Source { get; set; }
    public string? Revision { get; set; }
    public ProviderLyricsMetadata? Metadata { get; set; }
    public IReadOnlyList<LyricsInfo>? Versions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProviderLyricsAttribution? Attribution { get; set; }
    public IReadOnlyList<ProviderLyricsTimedLine>? TimedLines { get; set; }
}
