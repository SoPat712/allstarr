namespace allstarr.Models.Scrobbling;

public record ScrobbleTrack
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? DurationSeconds { get; init; }
    public string? MusicBrainzId { get; init; }
    public long? Timestamp { get; init; }
    public bool ChosenByUser { get; init; } = true;
    public bool IsExternal { get; init; }
    public int? StartPositionSeconds { get; init; }
}
