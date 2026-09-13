namespace allstarr.Models.Subsonic;

public sealed class ExternalPlaylist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CuratorName { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public int Duration { get; set; }
    public string? CoverUrl { get; set; }
    public DateTime? CreatedDate { get; set; }
}
